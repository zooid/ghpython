﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using GhPython.DocReplacement;
using GhPython.Properties;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Parameters.Hints;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Runtime;
using System.IO;

namespace GhPython.Component
{
  public abstract class ScriptingAncestorComponent : SafeComponent
  {
    static bool g_resources_unpacked = false;
    internal static GrasshopperDocument g_document = new GrasshopperDocument();

    private readonly StringList m_py_output = new StringList(); // python output stream is piped here

    internal ComponentIOMarshal m_marshal;
    protected PythonScript m_py;
    private PythonCompiledCode m_compiled_py;
    protected string m_previousRunCode;
    protected PythonEnvironment m_env;
    private bool m_inDocStringsMode;
    protected string m_inner_codeInput = string.Empty;

    internal const string DOCUMENT_NAME = "ghdoc";
    private const string PARENT_ENVIRONMENT_NAME = "ghenv";

    #region Setup

    const string DESCRIPTION = "A python scriptable component";

    protected ScriptingAncestorComponent()
      : base("Python Script", "Python", DESCRIPTION, "Math", "Script")
    {
    }

    private static void UnpackScriptResources()
    {
      if (g_resources_unpacked)
        return;
      Guid python_plugin_id = new Guid("814d908a-e25c-493d-97e9-ee3861957f49");
      var plugin = Rhino.PlugIns.PlugIn.Find(python_plugin_id);
      if (plugin == null)
        return;

      g_resources_unpacked = true;

      // Unpack ghcomponents.py to the Python plug directory
      string settings_directory = plugin.SettingsDirectory;
      string marker_file = Path.Combine(settings_directory, "ghpy_version.txt");
      try
      {
        if (File.Exists(marker_file))
        {
          string text = File.ReadAllText(marker_file);
          Version markedversion = new Version(text);
          Version this_version = typeof(ZuiPythonComponent).Assembly.GetName().Version;
          if (markedversion == this_version)
          {
#if !DEBUG
            // everything looks good, bail out
            return;
#endif
          }
        }
      }
      catch (Exception ex)
      {
        HostUtils.ExceptionReport(ex);
      }

      try
      {
        // if we get to here, we need to unpack the resources
        if (!Directory.Exists(settings_directory))
          Directory.CreateDirectory(settings_directory);
        string ghpython_package_dir = Path.Combine(settings_directory, "lib", PythonEnvironment.GHPYTHONLIB_NAME);
        if (Directory.Exists(ghpython_package_dir))
          Directory.Delete(ghpython_package_dir, true);
        Directory.CreateDirectory(ghpython_package_dir);
        System.Reflection.Assembly a = typeof(ZuiPythonComponent).Assembly;
        string[] names = a.GetManifestResourceNames();

        const string prefix = "GhPython.package.";

        foreach (string name in names)
        {
          if (!name.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
            continue;

          Stream resource_stream = a.GetManifestResourceStream(name);
          if (resource_stream == null)
            continue;
          StreamReader stream = new StreamReader(resource_stream);
          string s = stream.ReadToEnd();
          stream.Close();
          string filename = name.Replace(prefix, "");
          string path = Path.Combine(ghpython_package_dir, filename);
          File.WriteAllText(path, s);
        }

        // Write the marker file at the very end to ensure that we actually got to this point.
        // If an exception occured for some reason like a file was in use, then the plug-in
        // will just attempt to unpack the resources next time.
        string str = a.GetName().Version.ToString();
        File.WriteAllText(marker_file, str);
      }
      catch (Exception ex)
      {
        HostUtils.DebugString("Exception while unpacking resources: " + ex.Message);
      }
    }

    protected override void Initialize()
    {
      base.Initialize();

      if (Doc != null)
        Doc.SolutionEnd += OnDocSolutionEnd;

      m_py = PythonScript.Create();
      if (m_py != null)
      {
        SetScriptTransientGlobals();
        m_py.Output = m_py_output.Write;
        m_py.SetVariable("__name__", "__main__");
        m_env = new PythonEnvironment(this, m_py);

        m_py.SetVariable(PARENT_ENVIRONMENT_NAME, m_env);
        m_py.SetIntellisenseVariable(PARENT_ENVIRONMENT_NAME, m_env);

        m_py.ContextId = 2; // 2 is Grasshopper

        m_env.LoadAssembly(typeof(GH_Component).Assembly); //add Grasshopper.dll reference
        m_env.LoadAssembly(typeof(ZuiPythonComponent).Assembly); //add GHPython.dll reference

        UnpackScriptResources();

        m_env.AddGhPythonPackage();
      }
    }
    #endregion

    #region Input and Output

    /// <summary>
    /// Show/Hide the "code" input in this component.  This is not something that
    /// users typically do NOT need to work with so it defaults to OFF and is only useful
    /// when users are dynamically generating scripts through some other component
    /// </summary>
    public bool HiddenCodeInput
    {
      get
      {
        if (Params.Input.Count < 1)
          return true;
        return !(Params.Input[0] is Grasshopper.Kernel.Parameters.Param_String);
      }
      set
      {
        if (value != HiddenCodeInput)
        {
          if (value)
          {
            m_inner_codeInput = Code;
            Params.UnregisterParameter(Params.Input[0]);
          }
          else
          {
            var param = ConstructCodeInputParameter();
            param.SetPersistentData(new GH_String(Code));
            Params.RegisterInputParam(param, 0);
          }
          Params.OnParametersChanged();
          OnDisplayExpired(true);

          if (!value)
            ExpireSolution(true);
        }
      }
    }

    public string Code
    {
      get
      {
        if (HiddenCodeInput)
          return m_inner_codeInput;

        return ScriptingAncestorComponent.ExtractCodeString((Param_String)Params.Input[0]);
      }
      set
      {
        if (!HiddenCodeInput)
          throw new InvalidOperationException("Cannot assign to code while code parameter exists");

        m_inner_codeInput = value ?? string.Empty;
        m_compiled_py = null;
      }
    }

    /// <summary>
    /// Returns true if the "code" input parameter is visible AND wired up to
    /// another component
    /// </summary>
    /// <returns></returns>
    public bool IsCodeInputLinked()
    {
      if (HiddenCodeInput)
        return false;
      var param = Params.Input[0];
      return param.SourceCount > 0;
    }

    public Param_String CodeInputParam
    {
      get
      {
        if (!HiddenCodeInput)
          return (Param_String)Params.Input[0];
        return null;
      }
    }

    /// <summary>
    /// Show/Hide the "out" output in this component.  This is not something that
    /// users typically need to work with so it defaults to ON to help debugging.
    /// </summary>
    public bool HiddenOutOutput
    {
      get
      {
        if (Params.Output.Count < 1)
          return true;
        return !(Params.Output[0] is Grasshopper.Kernel.Parameters.Param_String);
      }
      set
      {
        if (value != HiddenOutOutput)
        {
          if (value)
          {
            Params.UnregisterParameter(Params.Output[0]);
          }
          else
          {
            var param = ConstructOutOutputParam();
            Params.RegisterOutputParam(param, 0);
          }
          Params.OnParametersChanged();
          OnDisplayExpired(true);

          if (value)
            ExpireSolution(true);
        }
      }
    }
    

    protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
    {
      //pManager.RegisterParam(ConstructCodeInputParameter()); should not show by default
      AddDefaultInput(pManager);
    }

    protected abstract void AddDefaultInput(GH_Component.GH_InputParamManager pManager);

    public Param_String ConstructCodeInputParameter()
    {
      var code = new Param_String
        {
          Name = "code",
          NickName = "code",
          Description = "Python script to execute",
        };
      return code;
    }

    protected abstract void AddDefaultOutput(GH_Component.GH_OutputParamManager pManager);

    protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
    {
      pManager.RegisterParam(ConstructOutOutputParam());

      AddDefaultOutput(pManager);
      VariableParameterMaintenance();
    }

    public abstract void VariableParameterMaintenance();

    private static Param_String ConstructOutOutputParam()
    {
      var outText = new Param_String
        {
          Name = "out",
          NickName = "out",
          Description = "The execution information, as output and error streams",
        };
      return outText;
    }

    protected static IGH_TypeHint[] PossibleHints =
      {
        new GH_HintSeparator(),
        new GH_BooleanHint_CS(), new GH_IntegerHint_CS(), new GH_DoubleHint_CS(), new GH_ComplexHint(),
        new GH_StringHint_CS(), new GH_DateTimeHint(), new GH_ColorHint(),
        new GH_HintSeparator(),
        new GH_Point3dHint(),
        new GH_Vector3dHint(), new GH_PlaneHint(), new GH_IntervalHint(),
        new GH_UVIntervalHint()
      };

    internal abstract void FixGhInput(Param_ScriptVariable i, bool alsoSetIfNecessary = true);

    #endregion

    #region Solving

    protected override void SafeSolveInstance(IGH_DataAccess da)
    {
      m_env.DataAccessManager = da;

      if (m_py == null)
      {
        da.SetData(0, "No Python engine available. This component needs Rhino v5");
        return;
      }

      if(!HiddenOutOutput)
        da.DisableGapLogic(0);

      m_py_output.Reset();

      var rhdoc = RhinoDoc.ActiveDoc;
      var prevEnabled = (rhdoc != null) && rhdoc.Views.RedrawEnabled;
      
      try
      {
        // set output variables to "None"
        for (int i = HiddenOutOutput ? 0 : 1; i < Params.Output.Count; i++)
        {
          string varname = Params.Output[i].NickName;
          m_py.SetVariable(varname, null);
        }

        // caching variable to keep things as fast as possible
        bool showing_code_input = !HiddenCodeInput;
        // Set all input variables. Even null variables may be used in the
        // script, so do not attempt to skip these for optimization purposes.
        // Skip "Code" input parameter
        // Please pay attention to the input data structure type
        for (int i = showing_code_input ? 1 : 0; i < Params.Input.Count; i++)
        {
          string varname = Params.Input[i].NickName;
          object o = m_marshal.GetInput(da, i);
          m_py.SetVariable(varname, o);
          m_py.SetIntellisenseVariable(varname, o);
        }

        // the "code" string could be embedded in the component itself
        if (showing_code_input || m_compiled_py == null)
        {
          string script;
          if (!showing_code_input)
            script = Code;
          else
          {
            script = null;
            da.GetData(0, ref script);
          }

          if (string.IsNullOrWhiteSpace(script))
          {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No script to execute");
            return;
          }

          if (m_compiled_py == null ||
              string.Compare(script, m_previousRunCode, StringComparison.InvariantCulture) != 0)
          {
            if (!(m_inDocStringsMode = DocStringUtils.FindApplyDocString(script, this)))
              ResetAllDescriptions();
            m_compiled_py = m_py.Compile(script);
            m_previousRunCode = script;
          }
        }

        if (m_compiled_py != null)
        {
          string localPath;
          bool added = AddLocalPath(out localPath);

          m_compiled_py.Execute(m_py);

          if (added) RemoveLocalPath(localPath);

          // Python script completed, attempt to set all of the
          // output paramerers
          for (int i = HiddenOutOutput ? 0 : 1; i < Params.Output.Count; i++)
          {
            string varname = Params.Output[i].NickName;
            object o = m_py.GetVariable(varname);
            m_marshal.SetOutput(o, da, i);
          }
        }
        else
        {
          m_py_output.Write("There was a permanent error parsing this script. Please report to giulio@mcneel.com.");
        }
      }
      catch (Exception ex)
      {
        AddErrorNicely(m_py_output, ex);
        SetFormErrorOrClearIt(da, m_py_output);
        throw;
      }
      finally
      {
        if (rhdoc != null && prevEnabled != rhdoc.Views.RedrawEnabled)
          rhdoc.Views.RedrawEnabled = true;
      }
      SetFormErrorOrClearIt(da, m_py_output);
    }

    private bool AddLocalPath(out string path)
    {
      string probe_location = g_document.Path;
      if (string.IsNullOrWhiteSpace(probe_location)) { path = null; return false; }

      probe_location = Path.GetDirectoryName(probe_location);
      if (!Directory.Exists(probe_location)) { path = null; return false; }

      var sys_path = GetSysPath();
      var there_was = sys_path.OfType<string>().Any(s => 
        string.Equals(s, probe_location, StringComparison.InvariantCultureIgnoreCase));

      if (!there_was) sys_path.Insert(0, probe_location);
      path = probe_location;
      return !there_was;
    }

    private void RemoveLocalPath(string location)
    {
      var sys_path = GetSysPath();
      if (sys_path.Count > 0 &&
        string.Equals(sys_path[0] as string, location, StringComparison.InvariantCultureIgnoreCase))
      {
        sys_path.RemoveAt(0);
      }
    }

    private System.Collections.IList GetSysPath()
    {
      var sys_path = (System.Collections.IList)m_py.EvaluateExpression("import sys", "sys.path");
      m_py.RemoveVariable("sys");
      return sys_path;
    }

    private void AddErrorNicely(StringList sw, Exception ex)
    {
      sw.Write(string.Format("Runtime error ({0}): {1}", ex.GetType().Name, ex.Message));

      string error = m_py.GetStackTraceFromException(ex);

      error = error.Replace(", in <module>, \"<string>\"", ", in script");
      error = error.Trim();

      sw.Write(error);
    }

    private void SetFormErrorOrClearIt(IGH_DataAccess DA, StringList sl)
    {
      var attr = Attributes;

      if (sl.Result.Count > 0)
      {
        if (!HiddenOutOutput)
          DA.SetDataList(0, sl.Result);
        attr.TrySetLinkedEditorHelpText(sl.ToString());
      }
      else
      {
        attr.TrySetLinkedEditorHelpText("Execution completed successfully.");
      }
    }

    public static string ExtractCodeString(Param_String inputCode)
    {
      if (inputCode.VolatileDataCount > 0 && inputCode.VolatileData.PathCount > 0)
      {
        var goo = inputCode.VolatileData.get_Branch(0)[0] as IGH_Goo;
        if (goo != null)
        {
          string code;
          if (goo.CastTo(out code) && !string.IsNullOrEmpty(code))
            return code;
        }
      }
      else if (inputCode.PersistentDataCount > 0) // here to handle the lock and disabled components
      {
        var flat_list = inputCode.PersistentData.FlattenData();
        if (flat_list != null && flat_list.Count > 0)
        {
          var stringData = flat_list[0];
          if (stringData != null && !string.IsNullOrEmpty(stringData.Value))
          {
            return stringData.Value;
          }
        }
      }
      return string.Empty;
    }

    private void ResetAllDescriptions()
    {
      Description = DESCRIPTION;

      for (int i = !HiddenCodeInput ? 0 : 1; i < Params.Input.Count; i++)
      {
        Params.Input[i].Description = "Script input " + Params.Input[i].NickName + ".";
      }
      for (int i = HiddenOutOutput ? 0 : 1; i < Params.Output.Count; i++)
      {
        Params.Output[i].Description = "Script output " + Params.Output[i].NickName + ".";
      }

      AdditionalHelpFromDocStrings = null;
    }

    protected virtual void SetScriptTransientGlobals()
    {
    }

    private void OnDocSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      if (g_document != null)
        g_document.Objects.Clear();
    }

    internal void RemoveAllVariables()
    {
      var variables = m_py.GetVariableNames().ToArray();

      for (int i = 0; i < variables.Length; i++)
      {
        var variable = variables[i];

        if (string.IsNullOrWhiteSpace(variable)) continue;
        if (variable.StartsWith("_")) continue;
        if (variable == DOCUMENT_NAME || variable == PARENT_ENVIRONMENT_NAME) continue;
        
        m_py.RemoveVariable(variables[i]);
      }
    }

    #endregion

    #region Appearance

    public Point? DefaultEditorLocation { get; set; }
    public Size DefaultEditorSize { get; set; }

    public override void CreateAttributes()
    {
      base.Attributes = new PythonComponentAttributes(this);
    }

    internal new PythonComponentAttributes Attributes
    {
      get
      { return (PythonComponentAttributes)base.Attributes; }
    }

    public Control CreateEditorControl(Action<string> helpCallback)
    {
      if (m_py == null) return null;
      var control = m_py.CreateTextEditorControl("", helpCallback);
      return (Control) control;
    }
    
    protected override Bitmap Icon
    {
      get { return Resources.python; }
    }

    public override GH_Exposure Exposure
    {
      get { return GH_Exposure.secondary; }
    }

    public override bool AppendMenuItems(ToolStripDropDown iMenu)
    {
      var toReturn = base.AppendMenuItems(iMenu);

      try
      {
        {
          var t0 = new ToolStripMenuItem("Show \"code\" input parameter", GetCheckedImage(!HiddenCodeInput),
                                new TargetGroupToggler
                                {
                                  Component = this,
                                  Params = Params.Input,
                                  GetIsShowing = () => !HiddenCodeInput,
                                  SetIsShowing = value =>
                                  {
                                    HiddenCodeInput = !value;
                                  },
                                  Side = GH_ParameterSide.Input,
                                }.Toggle)
          {
            ToolTipText =
              string.Format("Code input is {0}. Click to {1} it.", HiddenCodeInput ? "shown" : "hidden",
                            HiddenCodeInput ? "hide" : "show"),
          };
          var t1 = new ToolStripMenuItem("Show output \"out\" parameter", GetCheckedImage(!HiddenOutOutput),
                                new TargetGroupToggler
                                {
                                  Component = this,
                                  Params = Params.Output,
                                  GetIsShowing = () => !HiddenOutOutput,
                                  SetIsShowing = value =>
                                  {
                                    HiddenOutOutput = !value;
                                  },
                                  Side = GH_ParameterSide.Output,
                                }.Toggle)
          {
            ToolTipText =
              string.Format("Print output is {0}. Click to {1} it.", HiddenOutOutput ? "hidden" : "shown",
                            HiddenOutOutput ? "show" : "hide"),
            Height = 32,
          };

          iMenu.Items.Insert(Math.Min(iMenu.Items.Count, 1), t0);
          iMenu.Items.Insert(Math.Min(iMenu.Items.Count, 2), t1);
        }


        {
          var tsi = new ToolStripMenuItem("&Open editor...", null, (sender, e) =>
          {
            var attr = Attributes;
            if (attr != null)
              attr.OpenEditor();
          });
          tsi.Font = new Font(tsi.Font, FontStyle.Bold);

          if (Locked) tsi.Enabled = false;

          iMenu.Items.Insert(Math.Min(iMenu.Items.Count, 3), tsi);
        }

        iMenu.Items.Insert(Math.Min(iMenu.Items.Count, 4), new ToolStripSeparator());

      }
      catch (Exception ex)
      {
        GhPython.Forms.PythonScriptForm.LastHandleException(ex);
      }

      return toReturn;
    }

    private class TargetGroupToggler
    {
      public ScriptingAncestorComponent Component { private get; set; }
      public List<IGH_Param> Params { private get; set; }
      public Func<bool> GetIsShowing { private get; set; }
      public Action<bool> SetIsShowing { private get; set; }
      public GH_ParameterSide Side { private get; set; }

      public void Toggle(object sender, EventArgs e)
      {
        try
        {
          SetIsShowing(!GetIsShowing());

          Component.Params.OnParametersChanged();
          Component.OnDisplayExpired(true);

          Component.ExpireSolution(true);
        }
        catch (Exception ex)
        {
          GhPython.Forms.PythonScriptForm.LastHandleException(ex);
        }
      }
    }


    protected static Bitmap GetCheckedImage(bool check)
    {
      return check ? Resources._checked : Resources._unchecked;
    }

    #endregion

    #region IO

    // used as keys for file IO
    const string ID_HideInput = "HideInput";
    const string ID_CodeInput = "CodeInput";
    const string ID_HideOutput = "HideOutput";
    const string ID_EditorLocation = "EditorLocation";
    const string ID_EditorSize = "EditorSize";

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
      bool rc = base.Write(writer);

      writer.SetBoolean(ID_HideInput, HiddenCodeInput);

      if (HiddenCodeInput)
        writer.SetString(ID_CodeInput, Code);

      writer.SetBoolean(ID_HideOutput, HiddenOutOutput);


      //update if possible and save editor location
      {
        Form editor;
        if (Attributes.TryGetEditor(out editor))
        {
          DefaultEditorLocation = editor.Location;
          DefaultEditorSize = editor.Visible ? editor.Size : editor.RestoreBounds.Size;
        }
      }
      if (DefaultEditorLocation != null)
      {
        writer.SetDrawingPoint(ID_EditorLocation, DefaultEditorLocation.Value);
        writer.SetDrawingSize(ID_EditorSize, DefaultEditorSize);
      }

      return rc;
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
      // 2013 Oct 8 - Giulio
      // Removing all hacks and making this work properly from Gh 0.9.61 onwards
      // The logic is this: this component ALWAYS gets constructed without "code" & with "out".
      // Then, when they are not necessary, these are added or removed.
      // RegisterInput/Output must always insert the original amount of items.


      if (reader.ItemExists(ID_EditorLocation))
        DefaultEditorLocation = reader.GetDrawingPoint(ID_EditorLocation);
      if (reader.ItemExists(ID_EditorSize))
        DefaultEditorSize = reader.GetDrawingSize(ID_EditorSize);

      bool hideInput = true;
      if (reader.TryGetBoolean(ID_HideInput, ref hideInput))
        HiddenCodeInput = hideInput;

      bool hideOutput = false;
      if (reader.TryGetBoolean(ID_HideOutput, ref hideOutput))
        HiddenOutOutput = hideOutput;

      if (hideInput)
        if (!reader.TryGetString(ID_CodeInput, ref m_inner_codeInput))
          m_inner_codeInput = string.Empty;

      bool rc = base.Read(reader);


      // Dynamic input fix for existing scripts
      // Always assign DynamicHint or Grasshopper
      // will set Line and not LineCurve, etc...
      if (Params != null && Params.Input != null)
      {
        for (int i = HiddenCodeInput ? 1 : 0; i < Params.Input.Count; i++)
        {
          var p = Params.Input[i] as Param_ScriptVariable;
          if (p != null)
          {
            FixGhInput(p, false);
            if (p.TypeHint == null)
            {
              p.TypeHint = p.Hints[0];
            }
          }
        }
      }
      return rc;
    }

    #endregion

    #region Help

    protected override string HelpDescription
    {
      get
      {
        if (m_inDocStringsMode)
        {
          if (string.IsNullOrEmpty(AdditionalHelpFromDocStrings))
            return base.HelpDescription;
          return base.HelpDescription +
                 "<br><br>\n<small>Remarks: <i>" +
                 DocStringUtils.Htmlify(AdditionalHelpFromDocStrings) +
                 "</i></small>";
        }
        return Resources.helpText;
      }
    }

    protected override string HtmlHelp_Source()
    {
      return base.HtmlHelp_Source().Replace("\nPython Script", "\n" + NickName);
    }

    public string AdditionalHelpFromDocStrings { get; set; }

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
      base.Dispose(disposing);

      if (disposing)
      {
        if (Doc != null)
          Doc.SolutionEnd -= OnDocSolutionEnd;

        var attr = Attributes;
        if (attr != null)
          attr.DisableLinkedEditor(true);
      }
    }
    #endregion

    #region Properties Access
    internal PythonScript PythonScript { get { return m_py; } }
    #endregion
  }
}