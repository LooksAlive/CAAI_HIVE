using System;
using Avalonia.Controls;

namespace CAAI;

public partial class MainMenuWidget : UserControl
{
    public event Action? OnNewNotebook;
    public event Action? OnNewProject;
    public event Action? OnOpenFile;
    public event Action? OnOpenProject;
    public event Action<string>? OnOpenRecentRequested;
    public event Action? OnClearRecentRequested;
    public event Action? OnSaveFile;
    public event Action? OnSaveAll;
    public event Action? OnExit;

    public event Action? OnUndo;
    public event Action? OnRedo;
    public event Action? OnCut;
    public event Action? OnCopy;
    public event Action? OnPaste;
    public event Action? OnToggleComment;
    public event Action? OnFormatDocument;

    // Use Action<bool> to pass the checked state directly 
    public event Action<bool>? OnToggleExplorer;
    public event Action<bool>? OnToggleStructure;
    public event Action<bool>? OnToggleWordWrap;
    public event Action<bool>? OnToggleOutput;

    public void SetOutputVisible(bool visible) => MenuToggleOutput.IsChecked = visible;

    public void SetRecentProjects(IEnumerable<string> paths)
    {
        MenuRecentProjects.Items.Clear();
        var recentProjects = paths.Take(10).ToArray();
        if (recentProjects.Length == 0)
        {
            MenuRecentProjects.Items.Add(new MenuItem { Header = "No recent projects", IsEnabled = false, Foreground = Avalonia.Media.Brushes.Gray });
            return;
        }

        foreach (var path in recentProjects)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar));
            var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var item = new MenuItem
            {
                Header = string.IsNullOrEmpty(parent) ? folder : $"{folder}  -  {parent}",
                Foreground = Avalonia.Media.Brushes.White
            };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => OnOpenRecentRequested?.Invoke(path);
            MenuRecentProjects.Items.Add(item);
        }

        MenuRecentProjects.Items.Add(new Separator());
        var clearRecent = new MenuItem { Header = "Clear Recent List", Foreground = Avalonia.Media.Brushes.White };
        clearRecent.Click += (_, _) => OnClearRecentRequested?.Invoke();
        MenuRecentProjects.Items.Add(clearRecent);
    }

    public event Action? OnRunProject;
    public event Action? OnRunNoDebug;
    public event Action? OnStepInto;
    public event Action? OnStepOver;
    public event Action? OnToggleBreakpoint;
    public event Action? OnPauseDebug;
    public event Action? OnStopDebug;
    public event Action? OnRestartDebug;
    public event Action? OnStepOut;
    public event Action? OnSelectDebugProject;
    public event Action? OnDebugSettings;

    public void SetDebugState(DebugSessionState state)
    {
        var idle = state is DebugSessionState.Idle or DebugSessionState.Faulted;
        MenuRunProject.Header = state == DebugSessionState.Paused ? "_Continue" : "_Start Debugging";
        MenuRunProject.IsEnabled = idle || state == DebugSessionState.Paused;
        MenuRunNoDebug.IsEnabled = MenuDebugProject.IsEnabled = MenuDebugSettings.IsEnabled = idle;
        MenuDebugPause.IsEnabled = state == DebugSessionState.Running;
        MenuDebugStop.IsEnabled = !idle;
        MenuDebugRestart.IsEnabled = state is DebugSessionState.Running or DebugSessionState.Paused;
        MenuStepInto.IsEnabled = MenuStepOver.IsEnabled = MenuStepOut.IsEnabled = state == DebugSessionState.Paused;
    }

    public event Action? OnOpenSettings;
    public event Action? OnOpenNuGetManager;
    public event Action? OnOpenAbout;

    public MainMenuWidget()
    {
        InitializeComponent();
        
        // === File Subscriptions ===
        MenuNewNotebook.Click += (s, e) => OnNewNotebook?.Invoke();
        MenuNewProject.Click  += (s, e) => OnNewProject?.Invoke();
        MenuOpenFile.Click    += (s, e) => OnOpenFile?.Invoke();
        MenuOpenProject.Click += (s, e) => OnOpenProject?.Invoke();
        MenuSaveFile.Click    += (s, e) => OnSaveFile?.Invoke();
        MenuSaveAll.Click     += (s, e) => OnSaveAll?.Invoke();
        MenuExit.Click        += (s, e) => OnExit?.Invoke();

        // === Edit Subscriptions ===
        MenuUndo.Click          += (s, e) => OnUndo?.Invoke();
        MenuRedo.Click          += (s, e) => OnRedo?.Invoke();
        MenuCut.Click           += (s, e) => OnCut?.Invoke();
        MenuCopy.Click          += (s, e) => OnCopy?.Invoke();
        MenuPaste.Click         += (s, e) => OnPaste?.Invoke();
        MenuToggleComment.Click += (s, e) => OnToggleComment?.Invoke();
        MenuFormatDoc.Click     += (s, e) => OnFormatDocument?.Invoke();

        // === View Subscriptions (Passing state safely) ===
        MenuToggleExplorer.Click += (s, e) => OnToggleExplorer?.Invoke(MenuToggleExplorer.IsChecked);
        MenuToggleStructure.Click += (s, e) => OnToggleStructure?.Invoke(MenuToggleStructure.IsChecked);
        MenuToggleWordWrap.Click += (s, e) => OnToggleWordWrap?.Invoke(MenuToggleWordWrap.IsChecked);
        MenuToggleOutput.Click += (s, e) => OnToggleOutput?.Invoke(MenuToggleOutput.IsChecked);

        // === Run / Debug Subscriptions ===
        MenuRunProject.Click      += (s, e) => OnRunProject?.Invoke();
        MenuRunNoDebug.Click      += (s, e) => OnRunNoDebug?.Invoke();
        MenuStepInto.Click        += (s, e) => OnStepInto?.Invoke();
        MenuStepOver.Click        += (s, e) => OnStepOver?.Invoke();
        MenuToggleBreakpoint.Click += (s, e) => OnToggleBreakpoint?.Invoke();
        MenuDebugPause.Click += (_, _) => OnPauseDebug?.Invoke();
        MenuDebugStop.Click += (_, _) => OnStopDebug?.Invoke();
        MenuDebugRestart.Click += (_, _) => OnRestartDebug?.Invoke();
        MenuStepOut.Click += (_, _) => OnStepOut?.Invoke();
        MenuDebugProject.Click += (_, _) => OnSelectDebugProject?.Invoke();
        MenuDebugSettings.Click += (_, _) => OnDebugSettings?.Invoke();

        // === Tools / Help Subscriptions ===
        MenuNuget.Click += (s, e) => OnOpenNuGetManager?.Invoke();
        MenuSettings.Click += (s, e) => OnOpenSettings?.Invoke();
        MenuAbout.Click    += (s, e) => OnOpenAbout?.Invoke();
    }
}