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

    public event Action? OnRunProject;
    public event Action? OnRunNoDebug;
    public event Action? OnStepInto;
    public event Action? OnStepOver;
    public event Action? OnToggleBreakpoint;

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

        // Recent items execution
        MenuRecent1.Click     += (s, e) => OnOpenRecentRequested?.Invoke(MenuRecent1.Header?.ToString() ?? "");
        MenuRecent2.Click     += (s, e) => OnOpenRecentRequested?.Invoke(MenuRecent2.Header?.ToString() ?? "");
        MenuClearRecent.Click += (s, e) => OnClearRecentRequested?.Invoke();

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

        // === Tools / Help Subscriptions ===
        MenuNuget.Click += (s, e) => OnOpenNuGetManager?.Invoke();
        MenuSettings.Click += (s, e) => OnOpenSettings?.Invoke();
        MenuAbout.Click    += (s, e) => OnOpenAbout?.Invoke();
    }
}