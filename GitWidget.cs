using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace CAAI;

public sealed class GitWidget : UserControl
{
    private string _directory;
    private readonly Action<string> _openFile;
    private readonly Func<string, Action> _prepareWorkTreeChange;
    private readonly StackPanel _body = new() { Spacing = 10, Margin = new Thickness(10, 8, 10, 10) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
    private readonly TextBlock _repository = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly ListBox _branches = new() { Height = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _commits = new() { Height = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _continueRebase;
    private readonly Button _abortRebase;
    private readonly TextBox _newBranch = new() { Watermark = "New branch name", MinWidth = 0 };
    private readonly TextBox _commitMessage = new() { Watermark = "Commit message", AcceptsReturn = true, MinHeight = 64, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _staged = new() { Spacing = 2 };
    private readonly StackPanel _changes = new() { Spacing = 2 };
    private readonly TextBlock _stagedHeading = new();
    private readonly TextBlock _changesHeading = new();
    private readonly Button _initialize = new() { Content = "Initialize repository", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _commitButton;
    private readonly Button _createBranchButton;
    private readonly MenuFlyout _moreActions;
    private string? _root;
    private string _currentBranch = "";
    private bool _busy;
    private bool _refreshPending;

    public event Action<string>? StatusChanged;

    public GitWidget(string directory, Action<string> openFile, Func<string, Action> prepareWorkTreeChange)
    {
        _directory = directory;
        _openFile = openFile;
        _prepareWorkTreeChange = prepareWorkTreeChange;

        // ---- Toolbar (compact primary actions + overflow) ----
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var actions = new WrapPanel();
        actions.Children.Add(Tool(Symbol.Refresh, "Refresh", Refresh));
        actions.Children.Add(Tool(Symbol.Download, "Fetch all", () => RunAction("Fetch", "fetch", "--all", "--prune")));
        actions.Children.Add(Tool(Symbol.ChevronDown, "Pull (fast-forward only)", () => ChangeWorkTree("Pull", "pull", "--ff-only")));
        actions.Children.Add(Tool(Symbol.Up, "Push", () => RunAction("Push", "push")));
        toolbar.Children.Add(actions);

        // Overflow "..." menu with all the branch/advanced operations
        _moreActions = BuildMoreActions();
        var moreButton = new Button
        {
            Content = new SymbolIcon { Symbol = Symbol.More, FontSize = 16 },
            Width = 32, Height = 32, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Flyout = _moreActions
        };
        ToolTip.SetTip(moreButton, "More actions");
        Avalonia.Automation.AutomationProperties.SetName(moreButton, "More actions");
        Grid.SetColumn(moreButton, 1);
        toolbar.Children.Add(moreButton);

        // ---- Branch row: switch / delete / rebase controls kept inline but compact ----
        var branchActions = new WrapPanel();
        branchActions.Children.Add(Tool(Symbol.Forward, "Switch to selected branch", SwitchBranch));
        branchActions.Children.Add(Tool(Symbol.Delete, "Delete selected merged branch", DeleteBranch));
        branchActions.Children.Add(Tool(Symbol.Repair, "Rebase current branch onto selected branch", StartRebase));
        _continueRebase = Tool(Symbol.Play, "Continue rebase", ContinueRebase);
        _abortRebase = Tool(Symbol.Stop, "Abort rebase", AbortRebase);
        branchActions.Children.Add(_continueRebase);
        branchActions.Children.Add(_abortRebase);
        _continueRebase.IsVisible = false;
        _abortRebase.IsVisible = false;

        var createRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        createRow.Children.Add(_newBranch);
        _createBranchButton = Tool(Symbol.Add, "Create and switch branch", CreateBranch);
        Grid.SetColumn(_createBranchButton, 1);
        createRow.Children.Add(_createBranchButton);

        _commitButton = new Button
        {
            Content = "Commit staged changes",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "accent" }
        };
        _commitButton.Click += (_, _) => Commit();

        _initialize.Click += (_, _) => Initialize();

        // ---- Assemble body ----
        _body.Children.Add(toolbar);
        _body.Children.Add(_repository);
        _body.Children.Add(new TextBlock { Text = "Branches", FontWeight = FontWeight.SemiBold });
        _body.Children.Add(_branches);
        _body.Children.Add(branchActions);
        _body.Children.Add(createRow);
        _body.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        _body.Children.Add(_commitMessage);
        _body.Children.Add(_commitButton);
        _body.Children.Add(Section(_stagedHeading, Symbol.Remove, "Unstage all", () => RunAction("Unstage all", "reset", "-q", "--", ".")));
        _body.Children.Add(_staged);
        _body.Children.Add(Section(_changesHeading, Symbol.Add, "Stage all", () => RunAction("Stage all", "add", "-A", "--", ".")));
        _body.Children.Add(_changes);
        _body.Children.Add(Section(new TextBlock { Text = "Recent commits" }, Symbol.Document, "View selected commit", ShowSelectedCommit));
        _commits.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) => new StackPanel
        {
            Margin = new Thickness(2),
            Children =
            {
                new TextBlock { Text = $"{commit.ShortId}  {commit.Subject}", TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{commit.Author}  |  {commit.Date}", FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        });
        _commits.DoubleTapped += (_, _) => ShowSelectedCommit();
        _body.Children.Add(_commits);
        _body.Children.Add(_initialize);
        _body.Children.Add(_message);
        Content = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AttachedToVisualTree += (_, _) => Refresh();
    }
    
    private MenuFlyout BuildMoreActions()
    {
        var flyout = new MenuFlyout();

        void Add(string header, Symbol? icon, Action action)
        {
            var item = new MenuItem { Header = header };
            if (icon != null)
            {
                item.Icon = new SymbolIcon
                {
                    Symbol = icon.Value,
                    FontSize = 14
                };
            }
            item.Click += (_, _) => action();
            flyout.Items.Add(item);
        }
        
        void AddSeparator() => flyout.Items.Add(new Separator());

        Add("Fetch (all remotes, prune)", Symbol.Download, () => RunAction("Fetch", "fetch", "--all", "--prune"));
        Add("Pull (fast-forward only)", Symbol.ChevronDown, () => ChangeWorkTree("Pull", "pull", "--ff-only"));
        Add("Pull (rebase)", Symbol.Sync, () => ChangeWorkTree("Pull (rebase)", "pull", "--rebase"));
        Add("Push", Symbol.Up, () => RunAction("Push", "push"));
        Add("Push (set upstream)", Symbol.Upload, PushSetUpstream);
        AddSeparator();
        Add("Switch to selected branch", Symbol.Forward, SwitchBranch);
        Add("Create & switch branch", Symbol.Add, CreateBranch);
        Add("Delete selected branch", Symbol.Delete, DeleteBranch);
        Add("Rename current branch…", Symbol.Rename, RenameBranch);
        AddSeparator();
        Add("Rebase onto selected branch", Symbol.Repair, StartRebase);
        Add("Merge selected branch into current", Symbol.Flag, MergeBranch); // Merge
        Add("Continue rebase", Symbol.Play, ContinueRebase);
        Add("Abort rebase", Symbol.Stop, AbortRebase);
        AddSeparator();
        Add("Stash all changes", Symbol.Save, () => RunAction("Stash", "stash", "push", "-u"));
        Add("Pop latest stash", Symbol.Undo, () => RunAction("Stash pop", "stash", "pop"));
        AddSeparator();
        Add("Discard all working-tree changes", Symbol.Cancel, DiscardAllChanges);
        Add("Reset to HEAD (soft)", Symbol.Redo, () => RunAction("Reset", "reset", "--soft", "HEAD")); // Rewind
        AddSeparator();
        Add("Open repository folder", Symbol.Folder, OpenRepositoryFolder);

        return flyout;
    }

    private static Button Tool(Symbol icon, string label, Action action)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = icon, FontSize = 16 },
            Width = 32, Height = 32, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static Control Section(TextBlock heading, Symbol icon, string label, Action action)
    {
        heading.FontWeight = FontWeight.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(heading);
        var button = Tool(icon, label, action);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    public void SetDirectory(string directory)
    {
        _directory = directory;
        Refresh();
    }

    public async void Refresh()
    {
        if (_busy) { _refreshPending = true; return; }
        await ExecuteAsync(async () => await LoadAsync());
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _body.IsEnabled = false;
        try { await action(); }
        catch (Exception exception) { _message.Text = exception.Message; }
        finally
        {
            _body.IsEnabled = true;
            _busy = false;
            if (_refreshPending) { _refreshPending = false; Refresh(); }
        }
    }

    private async Task LoadAsync(string? message = null)
    {
        var discover = await GitAsync(_directory, "rev-parse", "--show-toplevel");
        if (discover.ExitCode != 0)
        {
            _root = null;
            _currentBranch = "";
            _initialize.IsVisible = true;
            _repository.Text = "No Git repository";
            _branches.ItemsSource = Array.Empty<string>();
            _commits.ItemsSource = Array.Empty<GitCommit>();
            _continueRebase.IsVisible = false;
            _abortRebase.IsVisible = false;
            _staged.Children.Clear();
            _changes.Children.Clear();
            _stagedHeading.Text = "Staged (0)";
            _changesHeading.Text = "Changes (0)";
            _commitButton.IsEnabled = false;
            StatusChanged?.Invoke("Git: no repository");
            _message.Text = discover.Error.Trim();
            return;
        }

        _root = discover.Output.TrimEnd('\r', '\n');
        _initialize.IsVisible = false;
        var status = await RequireGitAsync("status", "--porcelain=v1", "-z", "--untracked-files=all");
        var branch = await GitAsync(_root, "symbolic-ref", "--quiet", "--short", "HEAD");
        var current = branch.ExitCode == 0 ? branch.Output.TrimEnd('\r', '\n') : "(detached HEAD)";
        _currentBranch = current;
        var branches = await RequireGitAsync("for-each-ref", "--format=%(refname:short)", "refs/heads/");
        _repository.Text = $"{Path.GetFileName(_root)}  |  {current}";
        ToolTip.SetTip(_repository, _root);
        var selectedBranch = _branches.SelectedItem as string;
        var branchNames = branches.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimEnd('\r')).ToArray();
        _branches.ItemsSource = branchNames;
        _branches.SelectedItem = selectedBranch != null && branchNames.Contains(selectedBranch) ? selectedBranch : current;
        var rebaseMerge = await RequireGitAsync("rev-parse", "--git-path", "rebase-merge");
        var rebaseApply = await RequireGitAsync("rev-parse", "--git-path", "rebase-apply");
        var rebasing = Directory.Exists(Path.GetFullPath(rebaseMerge.TrimEnd('\r', '\n'), _root)) || Directory.Exists(Path.GetFullPath(rebaseApply.TrimEnd('\r', '\n'), _root));
        _continueRebase.IsVisible = rebasing;
        _abortRebase.IsVisible = rebasing;
        var head = await GitAsync(_root, "rev-parse", "--verify", "HEAD");
        if (head.ExitCode == 0)
        {
            var log = await RequireGitAsync("log", "--all", "-100", "--date=short", "-z", "--format=%H%x00%h%x00%an%x00%ad%x00%s");
            var fields = log.Split('\0');
            var commits = new List<GitCommit>();
            for (var index = 0; index + 4 < fields.Length; index += 5)
                commits.Add(new GitCommit(fields[index], fields[index + 1], fields[index + 2], fields[index + 3], fields[index + 4]));
            _commits.ItemsSource = commits;
        }
        else _commits.ItemsSource = Array.Empty<GitCommit>();
        var entries = ParseStatus(status);
        _staged.Children.Clear();
        _changes.Children.Clear();
        foreach (var entry in entries)
        {
            if (entry.Index != ' ' && entry.Index != '?') _staged.Children.Add(ChangeRow(entry, true));
            if (entry.WorkTree != ' ' || entry.Index == '?') _changes.Children.Add(ChangeRow(entry, false));
        }
        _stagedHeading.Text = $"Staged ({_staged.Children.Count})";
        _changesHeading.Text = $"Changes ({_changes.Children.Count})";
        _commitButton.IsEnabled = _staged.Children.Count > 0;
        var tracking = await GitAsync(_root, "rev-list", "--left-right", "--count", "HEAD...@{upstream}");
        var counts = tracking.ExitCode == 0 ? tracking.Output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var sync = counts.Length == 2 ? $"  |  ↑{counts[0]} ↓{counts[1]}" : "";
        StatusChanged?.Invoke($"Git: {current}{(rebasing ? " (rebasing)" : "")}  |  {_staged.Children.Count} staged, {_changes.Children.Count} changed{sync}");
        _message.Text = message ?? (entries.Count == 0 ? "Working tree clean" : "");
    }

    private Control ChangeRow(GitEntry entry, bool staged)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 2 };

        // Status letter rendered as a small colored badge.
        var (letter, brush) = StatusBadge(staged ? entry.Index : entry.WorkTree);
        var badge = new Border
        {
            Background = new SolidColorBrush(brush, 0.18),
            BorderBrush = new SolidColorBrush(brush, 0.6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = letter, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(brush) }
        };

        var name = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    badge,
                    new TextBlock { Text = entry.Path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }
                }
            },
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(2),
            MinWidth = 0
        };
        ToolTip.SetTip(name, entry.Path);
        name.Click += (_, _) =>
        {
            var path = Path.Combine(_root!, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) _openFile(path);
            else ShowDiff(entry, staged);
        };
        row.Children.Add(name);

        var diff = Tool(Symbol.Document, "View diff", () => ShowDiff(entry, staged));
        Grid.SetColumn(diff, 1);
        row.Children.Add(diff);
        var toggle = Tool(staged ? Symbol.Remove : Symbol.Add, staged ? "Unstage" : "Stage", () => ToggleStage(entry, staged));
        Grid.SetColumn(toggle, 2);
        row.Children.Add(toggle);
        return row;
    }

    private static (string Letter, Color Color) StatusBadge(char code) => code switch
    {
        'M' => ("M", Color.Parse("#E2A03F")), // modified - amber
        'A' => ("A", Color.Parse("#3FB950")), // added - green
        'D' => ("D", Color.Parse("#F85149")), // deleted - red
        'R' => ("R", Color.Parse("#58A6FF")), // renamed - blue
        'C' => ("C", Color.Parse("#A371F7")), // copied - purple
        '?' => ("U", Color.Parse("#8B949E")), // untracked
        'U' => ("!", Color.Parse("#F85149")), // conflict
        _   => (code.ToString(), Color.Parse("#8B949E"))
    };

    private async void ToggleStage(GitEntry entry, bool staged)
    {
        await ExecuteAsync(async () =>
        {
            var paths = entry.OriginalPath == null ? new[] { entry.Path } : new[] { entry.Path, entry.OriginalPath };
            var arguments = (staged ? new[] { "reset", "-q", "--" } : new[] { "add", "-A", "--" }).Concat(paths).ToArray();
            await RequireGitAsync(arguments);
            await LoadAsync(staged ? "Unstaged" : "Staged");
        });
    }

    private async void RunAction(string name, params string[] arguments)
    {
        await ExecuteAsync(async () =>
        {
            var output = await RequireGitAsync(arguments);
            await LoadAsync(string.IsNullOrWhiteSpace(output) ? $"{name} complete" : output.Trim());
        });
    }

    private async void ChangeWorkTree(string name, params string[] arguments)
    {
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            var complete = _prepareWorkTreeChange(_root);
            var output = await RequireGitAsync(arguments);
            complete();
            await LoadAsync(string.IsNullOrWhiteSpace(output) ? $"{name} complete" : output.Trim());
        });
    }

    private async void Initialize()
    {
        await ExecuteAsync(async () =>
        {
            if (_root != null) { _message.Text = "A repository is already open."; return; }
            var result = await GitAsync(_directory, "init");
            EnsureSuccess(result);
            await LoadAsync("Repository initialized");
        });
    }

    private async void Commit()
    {
        await ExecuteAsync(async () =>
        {
            var message = _commitMessage.Text?.Trim();
            if (string.IsNullOrWhiteSpace(message)) { _message.Text = "Enter a commit message."; return; }
            var result = await RequireGitAsync("commit", "-m", message);
            _commitMessage.Text = "";
            await LoadAsync(result.Trim());
        });
    }

    private void SwitchBranch()
    {
        if (_branches.SelectedItem is string branch) ChangeWorkTree("Switch branch", "switch", "--", branch);
        else _message.Text = "Select a branch.";
    }

    private async void DeleteBranch()
    {
        if (_branches.SelectedItem is not string branch) { _message.Text = "Select a branch."; return; }
        await ExecuteAsync(async () =>
        {
            if (!await ConfirmAsync("Delete branch", $"Delete the merged local branch '{branch}'?")) return;
            var output = await RequireGitAsync("branch", "-d", "--", branch);
            await LoadAsync(output.Trim());
        });
    }

    private async void RenameBranch()
    {
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            var name = await PromptAsync("Rename branch", $"Rename '{_currentBranch}' to:", _currentBranch);
            if (string.IsNullOrWhiteSpace(name) || name == _currentBranch) return;
            var complete = _prepareWorkTreeChange(_root);
            var output = await RequireGitAsync("branch", "-m", "--", name.Trim());
            complete();
            await LoadAsync(string.IsNullOrWhiteSpace(output) ? "Renamed" : output.Trim());
        });
    }

    private async void MergeBranch()
    {
        if (_branches.SelectedItem is not string branch) { _message.Text = "Select a branch to merge."; return; }
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            if (!await ConfirmAsync("Merge branch", $"Merge '{branch}' into '{_currentBranch}'?")) return;
            var complete = _prepareWorkTreeChange(_root);
            var output = await RequireGitAsync("merge", "--no-edit", "--", branch);
            complete();
            await LoadAsync(string.IsNullOrWhiteSpace(output) ? "Merge complete" : output.Trim());
        });
    }

    private async void PushSetUpstream()
    {
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            if (string.IsNullOrEmpty(_currentBranch) || _currentBranch.StartsWith("(")) { _message.Text = "No upstream branch detected."; return; }
            var output = await RequireGitAsync("push", "-u", "origin", _currentBranch);
            await LoadAsync(string.IsNullOrWhiteSpace(output) ? "Pushed" : output.Trim());
        });
    }

    private async void DiscardAllChanges()
    {
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            if (!await ConfirmAsync("Discard changes", "Discard all unstaged changes in the working tree? This cannot be undone.")) return;
            var complete = _prepareWorkTreeChange(_root);
            await RequireGitAsync("checkout", "--", ".");
            await RequireGitAsync("clean", "-fd");
            complete();
            await LoadAsync("Discarded working-tree changes");
        });
    }

    private void OpenRepositoryFolder()
    {
        if (_root == null) return;
        try
        {
            var start = new ProcessStartInfo { FileName = _root, UseShellExecute = true };
            Process.Start(start);
        }
        catch (Exception exception) { _message.Text = exception.Message; }
    }

    private async void StartRebase()
    {
        if (_branches.SelectedItem is not string branch) { _message.Text = "Select a branch to rebase onto."; return; }
        await RebaseAsync("Rebase", $"Rebase the current branch onto '{branch}'? This rewrites commits.", "rebase", "--", branch);
    }

    private async void ContinueRebase() => await RebaseAsync("Continue rebase", null, "rebase", "--continue");

    private async void AbortRebase() => await RebaseAsync("Abort rebase", "Abort the rebase and restore its original branch state?", "rebase", "--abort");

    private async Task RebaseAsync(string name, string? confirmation, params string[] arguments)
    {
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            if (confirmation != null && !await ConfirmAsync(name, confirmation)) return;
            var complete = _prepareWorkTreeChange(_root);
            var result = await GitAsync(_root, arguments);
            string? warning = null;
            try { complete(); }
            catch (Exception exception) { warning = exception.Message; }
            var message = result.ExitCode == 0 ? $"{name} complete" : (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim();
            if (warning != null) message += Environment.NewLine + warning;
            await LoadAsync(message);
        });
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var accept = new Button { Content = title, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        var accept = new Button { Content = title, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        accept.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(owner);
    }

    private async void CreateBranch()
    {
        await ExecuteAsync(async () =>
        {
            var name = _newBranch.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { _message.Text = "Enter a branch name."; return; }
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            var complete = _prepareWorkTreeChange(_root);
            await RequireGitAsync("switch", "-c", name, "--");
            complete();
            _newBranch.Text = "";
            await LoadAsync($"Switched to {name}");
        });
    }

    private async void ShowSelectedCommit()
    {
        await ExecuteAsync(async () =>
        {
            if (_commits.SelectedItem is not GitCommit commit) { _message.Text = "Select a commit."; return; }
            var details = await RequireGitAsync("show", "--format=fuller", "--no-ext-diff", "--no-color", commit.Id, "--");
            ShowText($"Commit {commit.ShortId}", details);
        });
    }

    private async void ShowDiff(GitEntry entry, bool staged)
    {
        await ExecuteAsync(async () =>
        {
            if (_root == null) throw new InvalidOperationException("No Git repository is open.");
            GitResult result;
            if (!staged && entry.Index == '?')
                result = await GitAsync(_root, "diff", "--no-index", "--", "/dev/null", entry.Path);
            else
            {
                var paths = entry.OriginalPath == null ? new[] { entry.Path } : new[] { entry.Path, entry.OriginalPath };
                var args = (staged ? new[] { "diff", "--no-ext-diff", "--cached", "--" } : new[] { "diff", "--no-ext-diff", "--" }).Concat(paths).ToArray();
                result = await GitAsync(_root, args);
            }
            if (result.ExitCode != 0 && !(entry.Index == '?' && result.ExitCode == 1)) EnsureSuccess(result);
            var text = string.IsNullOrEmpty(result.Output) ? "No textual diff available." : result.Output;
            ShowDiffWindow($"{(staged ? "Staged" : "Working tree")}: {entry.Path}", text);
        });
    }

    /// <summary>
    /// A richer diff viewer: a side-by-side / unified toggle with line coloring,
    /// line numbers and a summary header.
    /// </summary>
    private void ShowDiffWindow(string title, string diffText)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var (added, removed) = CountDiffLines(diffText);

        // Header
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 8, 10, 8)
        };
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(new TextBlock
        {
            Text = $" +{added}  −{removed} ",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(summary);

        // Unified view (line-numbered, color coded)
        var unified = BuildUnifiedDiffView(diffText);

        // Raw view toggle
        var rawBox = new TextBox
        {
            Text = diffText,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            IsVisible = false
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(rawBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(rawBox, ScrollBarVisibility.Auto);

        var modeButton = new ToggleButton { Content = "Raw", IsChecked = false, Padding = new Thickness(10, 4) };
        modeButton.IsCheckedChanged += (_, _) =>
        {
            var isRaw = modeButton.IsChecked == true;
            unified.IsVisible = !isRaw;
            rawBox.IsVisible = isRaw;
        };
        Grid.SetColumn(modeButton, 1);
        header.Children.Add(modeButton);

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        content.Children.Add(header);
        var body = new Grid();
        body.Children.Add(unified);
        body.Children.Add(rawBox);
        Grid.SetRow(body, 1);
        content.Children.Add(body);

        var dialog = new Window
        {
            Title = title,
            Width = 900,
            Height = 620,
            MinWidth = 400,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        _ = dialog.ShowDialog(owner);
    }

    private static ScrollViewer BuildUnifiedDiffView(string diffText)
    {
        var panel = new StackPanel { Spacing = 0, Margin = new Thickness(8, 0, 8, 8) };
        var lines = diffText.Replace("\r\n", "\n").Split('\n');
        int oldLine = 0, newLine = 0;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*")
        };

        var rows = new List<Control>();
        var mono = new FontFamily("Consolas");
        var fontSize = 12;

        // Track heading rows to render +/-
        foreach (var raw in lines)
        {
            var line = raw ?? "";
            Control? row = null;
            if (line.StartsWith("@@"))
            {
                // Hunk header: parse new start
                var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
                if (match.Success)
                {
                    oldLine = int.Parse(match.Groups[1].Value);
                    newLine = int.Parse(match.Groups[2].Value);
                }
                row = DiffRow("", "", line, Color.Parse("#1F3A5F"), Brushes.LightGray, mono, fontSize, true);
            }
            else if (line.StartsWith("+++") || line.StartsWith("---"))
            {
                row = DiffRow("", "", line, Colors.Transparent, Brushes.Gray, mono, fontSize);
            }
            else if (line.StartsWith("+"))
            {
                row = DiffRow(newLine.ToString(), "", line, Color.Parse("#1B4A2E"), Brush.Parse("#7EE787"), mono, fontSize);
                newLine++;
            }
            else if (line.StartsWith("-"))
            {
                row = DiffRow(oldLine.ToString(), "", line, Color.Parse("#4A1B1B"), Brush.Parse("#FFA198"), mono, fontSize);
                oldLine++;
            }
            else if (line.StartsWith(" "))
            {
                row = DiffRow(oldLine.ToString(), newLine.ToString(), line, Colors.Transparent, null, mono, fontSize);
                oldLine++;
                newLine++;
            }
            else
            {
                row = DiffRow("", "", line, Colors.Transparent, Brushes.Gray, mono, fontSize);
            }
            if (row != null) rows.Add(row);
        }

        foreach (var row in rows) panel.Children.Add(row);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static Control DiffRow(string oldNum, string newNum, string text, Color background, IBrush? foreground, FontFamily font, double fontSize, bool bold = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("48,48,*"), ColumnSpacing = 0 };
        grid.Children.Add(new TextBlock { Text = oldNum, FontFamily = font, FontSize = fontSize, Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 8, 1) });
        var newText = new TextBlock { Text = newNum, FontFamily = font, FontSize = fontSize, Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 8, 1) };
        Grid.SetColumn(newText, 1);
        grid.Children.Add(newText);
        var contentText = new TextBlock
        {
            Text = text,
            FontFamily = font,
            FontSize = fontSize,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = foreground ?? Brushes.LightGray,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(4, 1, 4, 1)
        };
        Grid.SetColumn(contentText, 2);
        grid.Children.Add(contentText);

        return new Border
        {
            Background = background == Colors.Transparent ? Brushes.Transparent : new SolidColorBrush(background),
            Child = grid
        };
    }

    private static (int Added, int Removed) CountDiffLines(string diffText)
    {
        int added = 0, removed = 0;
        foreach (var line in diffText.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("+") && !line.StartsWith("+++")) added++;
            else if (line.StartsWith("-") && !line.StartsWith("---")) removed++;
        }
        return (added, removed);
    }

    private void ShowText(string title, string text)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var editor = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var dialog = new Window
        {
            Title = title, Width = 850, Height = 600, MinWidth = 300, MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = editor
        };
        _ = dialog.ShowDialog(owner);
    }

    private async Task<string> RequireGitAsync(params string[] arguments)
    {
        if (_root == null) throw new InvalidOperationException("No Git repository is open.");
        var result = await GitAsync(_root, arguments);
        EnsureSuccess(result);
        return result.Output;
    }

    private static void EnsureSuccess(GitResult result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
    }

    private static async Task<GitResult> GitAsync(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        if (arguments.Length > 0 && arguments[0] == "rebase") info.Environment["GIT_EDITOR"] = "true";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Git.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, await output, await error);
    }

    private static List<GitEntry> ParseStatus(string status)
    {
        var entries = new List<GitEntry>();
        var records = status.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 4) continue;
            var original = (record[0] is 'R' or 'C' || record[1] is 'R' or 'C') && index + 1 < records.Length ? records[++index] : null;
            entries.Add(new GitEntry(record[3..], record[0], record[1], original));
        }
        return entries;
    }

    private sealed record GitEntry(string Path, char Index, char WorkTree, string? OriginalPath);
    private sealed record GitCommit(string Id, string ShortId, string Author, string Date, string Subject);
    private sealed record GitResult(int ExitCode, string Output, string Error);
}