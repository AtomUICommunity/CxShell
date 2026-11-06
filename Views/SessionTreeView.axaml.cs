using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CxShell.Models;
using CxShell.Controls;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace CxShell.Views;

public partial class SessionTreeView : UserControl
{
    private const string RowReorderHandleTypeName = "AtomUI.Desktop.Controls.DataGridRowReorderHandle";

    private static readonly MethodInfo? TryBeginRowReorderMethod =
        typeof(DataGrid).GetMethod("TryBeginRowReorder", BindingFlags.Instance | BindingFlags.NonPublic);

    private bool _selectionSyncPending;
    private int _selectionSyncGeneration;
    private SessionNodeViewModel? _selectionAnchorNode;
    private DataGridSourceAdapter<SessionNodeViewModel>? _gridSource;
    private DataGridKeyLookupSource? _sessionGridSource;

    private static string T(string key) => LocalizationService.Shared.Text(key);

    private static string Tf(string key, params object[] args) => string.Format(T(key), args);

    public SessionTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachGridSource();
        if (DataContext is not SessionTreeViewModel vm || SessionTree == null)
            return;

        _selectionSyncGeneration++;
        _selectionSyncPending = false;
        _gridSource = new DataGridSourceAdapter<SessionNodeViewModel>(vm.SessionRows);
        _sessionGridSource = new DataGridKeyLookupSource(_gridSource.Source, MoveSessionRowAsync);
        SessionTree.ItemsSource = _sessionGridSource;
        _gridSource.ConfigureColumns(SessionTree);
        _gridSource.SetSelectedItems(SessionTree, []);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_gridSource == null)
            OnDataContextChanged(this, EventArgs.Empty);
        _selectionSyncGeneration++;

        AddHandler(PointerWheelChangedEvent, OnTreePointerWheelChanged, RoutingStrategies.Tunnel);
        if (SessionTree != null)
        {
            SessionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
            SessionTree.SelectionChanged += OnTreeSelectionChanged;
        }
        UpdateColumnHeaders();
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;

        if (NewSessionBtn != null) NewSessionBtn.Click += OnNewClick;
        if (ImportSessionBtn != null) ImportSessionBtn.Click += OnImportClick;
        if (ExportSessionBtn != null) ExportSessionBtn.Click += OnExportClick;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _selectionSyncGeneration++;
        _selectionSyncPending = false;
        RemoveHandler(PointerWheelChangedEvent, OnTreePointerWheelChanged);
        if (SessionTree != null)
        {
            SessionTree.RemoveHandler(PointerPressedEvent, OnTreePointerPressed);
            SessionTree.SelectionChanged -= OnTreeSelectionChanged;
        }
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        DetachGridSource();
        base.OnUnloaded(e);
    }

    private void DetachGridSource()
    {
        if (SessionTree == null)
            return;

        SessionTree.ItemsSource = null;
        SessionTree.Selection = DataGridSelectionState.Empty;
        SessionTree.CurrentRowKey = null;
        _sessionGridSource?.Dispose();
        _sessionGridSource = null;
        _gridSource?.Dispose();
        _gridSource = null;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateColumnHeaders();
    }

    private void UpdateColumnHeaders()
    {
        if (SessionTree?.Columns.Count >= 6)
        {
            SessionTree.Columns[1].Header = LocalizationService.Shared.Text("SessionManager.ColumnName");
            SessionTree.Columns[2].Header = LocalizationService.Shared.Text("SessionManager.ColumnHost");
            SessionTree.Columns[3].Header = LocalizationService.Shared.Text("SessionManager.ColumnUsername");
            SessionTree.Columns[4].Header = LocalizationService.Shared.Text("SessionManager.ColumnProtocol");
            SessionTree.Columns[5].Header = LocalizationService.Shared.Text("SessionManager.ColumnPort");
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        if (SessionTree == null) return;

        var point = e.GetCurrentPoint(SessionTree);
        var node = FindClickedNode(e);

        // The tunnel handler must not consume presses on the row-reorder handle,
        // otherwise AtomUI never receives the pointer press that starts a drag.
        if (point.Properties.IsLeftButtonPressed && IsRowReorderHandleSource(e.Source))
            return;

        // Right-click opens the context menu.
        if (point.Properties.IsRightButtonPressed)
        {
            if (node != null)
            {
                var selectedNodes = GetSelectedNodes();
                if (!selectedNodes.Contains(node))
                {
                    SetGridSelectedItems([node]);
                    vm.SetSelectedNodes([node]);
                    _selectionAnchorNode = node;
                }
                else
                    vm.SetSelectedNodes(selectedNodes);
            }

            ShowSessionContextMenu(SessionTree, vm);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || node == null)
            return;

        SessionTree.Focus();
        ApplySessionSelection(vm, node, e.KeyModifiers);

        // AtomUI only starts a reorder from its small handle. Start the same
        // built-in session when the press lands anywhere else on the row.
        if (e.ClickCount == 1 && !IsInteractiveRowSource(e.Source))
        {
            var row = FindVisualAncestor<DataGridRow>(e.Source as Visual);
            if (row != null)
            {
                var handle = FindRowReorderHandle(row);
                if (handle != null)
                    TryBeginRowDrag(e, row, handle);
            }
        }

        e.Handled = true;

        if (e.ClickCount != 2 || node.Session == null)
            return;

        var mainVm = GetMainWindowViewModel();
        if (mainVm != null)
        {
            _ = mainVm.ConnectSession(node.Session);

            // Close the standalone session manager window after connecting.
            var window = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
            if (window is SessionManagerWindow)
                window.Close();
        }
    }

    private void ApplySessionSelection(
        SessionTreeViewModel vm,
        SessionNodeViewModel node,
        KeyModifiers modifiers)
    {
        var rows = vm.SessionRows.ToList();
        var selected = GetSelectedNodes().ToList();
        var hasCommandModifier = modifiers.HasFlag(KeyModifiers.Control) ||
                                 modifiers.HasFlag(KeyModifiers.Meta);
        var hasShift = modifiers.HasFlag(KeyModifiers.Shift);

        if (hasShift && _selectionAnchorNode != null)
        {
            var anchorIndex = rows.IndexOf(_selectionAnchorNode);
            var nodeIndex = rows.IndexOf(node);
            if (anchorIndex >= 0 && nodeIndex >= 0)
            {
                var start = Math.Min(anchorIndex, nodeIndex);
                var count = Math.Abs(anchorIndex - nodeIndex) + 1;
                var range = rows.Skip(start).Take(count).ToList();
                selected = hasCommandModifier
                    ? selected.Concat(range).Distinct().OrderBy(item => rows.IndexOf(item)).ToList()
                    : range;
            }
            else
            {
                selected = [node];
                _selectionAnchorNode = node;
            }
        }
        else if (hasCommandModifier)
        {
            if (selected.Contains(node))
                selected.Remove(node);
            else
                selected.Add(node);

            selected = selected.OrderBy(item => rows.IndexOf(item)).ToList();
            _selectionAnchorNode = node;
        }
        else
        {
            selected = [node];
            _selectionAnchorNode = node;
        }

        SetGridSelectedItems(selected);
        vm.SetSelectedNodes(selected);
    }

    private void SetGridSelectedItems(IReadOnlyList<SessionNodeViewModel> selected)
    {
        _gridSource?.SetSelectedItems(SessionTree, selected);
    }

    private async ValueTask<DataGridMutationResult> MoveSessionRowAsync(
        DataGridMoveRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            DataContext is not SessionTreeViewModel vm ||
            _gridSource == null ||
            !_gridSource.TryGetItem(request.RowKey, out var sourceNode))
        {
            return new DataGridMutationResult(null);
        }

        SessionNodeViewModel? targetNode = null;
        var insertAfter = false;
        if (request.BeforeKey is { } beforeKey &&
            _gridSource.TryGetItem(beforeKey, out var beforeNode))
        {
            targetNode = beforeNode;
        }
        else if (request.AfterKey is { } afterKey &&
                 _gridSource.TryGetItem(afterKey, out var afterNode))
        {
            targetNode = afterNode;
            insertAfter = true;
        }

        if (targetNode == null)
        {
            return new DataGridMutationResult(null);
        }

        // Let the local source perform the actual collection move so the grid
        // receives the new snapshot and does not stay in a stale drag state.
        var result = await _gridSource.MoveAsync(request, cancellationToken);
        vm.PersistSessionMove(sourceNode, targetNode, insertAfter);
        return result;
    }

    private void OnTreePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Scrolling can change the range snapshot and invalidate an active
        // AtomUI row-reorder session. Keep the drag alive while the pointer is
        // captured by the reorder handle.
        if (e.Pointer.Captured is Visual captured &&
            captured.GetType().FullName == RowReorderHandleTypeName)
        {
            e.Handled = true;
        }
    }

    private bool TryBeginRowDrag(PointerPressedEventArgs e, DataGridRow row, object handle)
    {
        if (TryBeginRowReorderMethod == null || SessionTree == null)
            return false;

        var rowsPresenter = SessionTree.GetVisualDescendants()
            .OfType<DataGridRowsPresenter>()
            .FirstOrDefault();
        if (rowsPresenter == null)
            return false;

        try
        {
            return TryBeginRowReorderMethod.Invoke(
                SessionTree,
                [handle, row, e.Pointer, e.GetPosition(rowsPresenter)]) is true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static object? FindRowReorderHandle(DataGridRow row) =>
        row.GetVisualDescendants()
            .FirstOrDefault(visual => visual.GetType().FullName == RowReorderHandleTypeName);

    private static T? FindVisualAncestor<T>(Visual? source) where T : Visual
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
                return match;
            current = current.GetVisualParent() as Visual;
        }

        return null;
    }

    private static bool IsInteractiveRowSource(object? source)
    {
        var current = source as Visual;
        while (current != null && current is not DataGridRow)
        {
            if (current is Avalonia.Controls.Button or
                Avalonia.Controls.TextBox or
                Avalonia.Controls.ComboBox or
                Avalonia.Controls.CheckBox or
                Avalonia.Controls.Slider)
            {
                return true;
            }
            current = current.GetVisualParent() as Visual;
        }

        return false;
    }

    private static bool IsRowReorderHandleSource(object? source) =>
        source is Visual visual && IsRowReorderHandle(visual);

    private static bool IsRowReorderHandle(Visual visual)
    {
        var current = visual;
        while (current != null)
        {
            if (current.GetType().FullName == RowReorderHandleTypeName)
                return true;
            current = current.GetVisualParent() as Visual;
        }

        return false;
    }

    private SessionNodeViewModel? FindClickedNode(PointerPressedEventArgs e)
    {
        var source = e.Source as Visual;
        if (source == null) return null;

        var current = source;
        while (current != null)
        {
            if (current.DataContext is SessionNodeViewModel node && node.Session != null)
            {
                return node;
            }
            current = current.GetVisualParent() as Visual;
        }

        return null;
    }

    private MainWindowViewModel? GetMainWindowViewModel()
    {
        // Prefer the view model's main window reference for standalone window mode.
        if (DataContext is SessionTreeViewModel vm)
            return vm.MainWindow;

        var window = TopLevel.GetTopLevel(this);
        return window?.DataContext as MainWindowViewModel;
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        if (!vm.CanUseSelectedSession) return;
        var session = vm.SelectedSession;
        if (session == null) return;

        var mainVm = GetMainWindowViewModel();
        if (mainVm != null)
        {
            await mainVm.EditSessionAsync(session);
        }
    }

    private void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var mainVm = GetMainWindowViewModel();
        mainVm?.NewSessionCommand.Execute(null);
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        if (!vm.CanUseSelectedSession) return;
        var session = vm.SelectedSession;
        if (session == null) return;

        var mainVm = GetMainWindowViewModel();
        if (mainVm != null)
        {
            await mainVm.ConnectSession(session);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        var sessions = vm.GetSelectedSessions();
        if (sessions.Count == 0 || !vm.CanDeleteSelectedSessions) return;

        var owner = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
        if (owner == null || !await ShowLocalizedDeleteConfirmWindow(owner, sessions))
            return;

        vm.DeleteSelectedSessions();
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm ||
            TopLevel.GetTopLevel(this) is not TopLevel topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("SessionManager.Import"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CxShell session export")
                {
                    Patterns = ["*.cxsessions.json", "*.json"]
                },
                new FilePickerFileType("OpenSSH config")
                {
                    Patterns = ["config", "*.conf"]
                },
                FilePickerFileTypes.All
            ]
        });
        var file = files.FirstOrDefault();
        if (file == null)
            return;

        try
        {
            var result = vm.ImportFile(file.Path.LocalPath);
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ImportSuccessTitle"),
                Tf(
                    result.IsOpenSshConfig
                        ? "SessionManager.ImportSshConfigSuccessMessage"
                        : "SessionManager.ImportSuccessMessage",
                    result.Count),
                MessageBoxStyle.Success);
        }
        catch (Exception ex)
        {
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ImportFailedTitle"),
                Tf("SessionManager.ImportFailedMessage", ex.Message),
                MessageBoxStyle.Error);
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm ||
            !vm.CanExportSelectedSessions ||
            TopLevel.GetTopLevel(this) is not TopLevel topLevel)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("SessionManager.Export"),
            SuggestedFileName = $"CxShell-Sessions-{DateTime.Now:yyyy-MM-dd}.cxsessions.json",
            DefaultExtension = "cxsessions.json",
            FileTypeChoices =
            [
                new FilePickerFileType("CxShell session export")
                {
                    Patterns = ["*.cxsessions.json"]
                }
            ]
        });
        if (file == null)
            return;

        try
        {
            vm.ExportSelectedSessions(file.Path.LocalPath);
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ExportSuccessTitle"),
                Tf("SessionManager.ExportSuccessMessage", vm.SelectedSessionCount),
                MessageBoxStyle.Success);
        }
        catch (Exception ex)
        {
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ExportFailedTitle"),
                Tf("SessionManager.ExportFailedMessage", ex.Message),
                MessageBoxStyle.Error);
        }
    }

    private void ShowSessionContextMenu(Control anchor, SessionTreeViewModel vm)
    {
        var menu = new AtomContextMenu
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor
        };

        void AddItem(
            string text,
            Func<System.Threading.Tasks.Task> action,
            bool isEnabled = true)
        {
            var item = new AtomMenuItem { Header = text, IsEnabled = isEnabled };
            item.Click += async (_, _) =>
            {
                menu.Close();
                await action();
            };
            menu.Items.Add(item);
        }

        AddItem(vm.ConnectText, () =>
        {
            OnConnectClick(anchor, new RoutedEventArgs());
            return System.Threading.Tasks.Task.CompletedTask;
        }, vm.CanUseSelectedSession);
        AddItem(vm.PropertiesText, () =>
        {
            OnEditClick(anchor, new RoutedEventArgs());
            return System.Threading.Tasks.Task.CompletedTask;
        }, vm.CanUseSelectedSession);
        menu.Items.Add(new AtomMenuSeparator());
        AddItem(vm.CopyText, () =>
        {
            CopySelectedSession(vm);
            return System.Threading.Tasks.Task.CompletedTask;
        }, vm.CanUseSelectedSession);
        if (vm.SelectedSession is { Protocol: SessionProtocol.SSH } session)
        {
            menu.Items.Add(new AtomMenuSeparator());
            AddItem(vm.DiagnosticsText, () =>
            {
                var mainVm = GetMainWindowViewModel();
                return mainVm?.ShowConnectionDiagnosticsAsync(session)
                    ?? System.Threading.Tasks.Task.CompletedTask;
            });
        }
        menu.Items.Add(new AtomMenuSeparator());
        AddItem(vm.DeleteText, () =>
        {
            OnDeleteClick(anchor, new RoutedEventArgs());
            return System.Threading.Tasks.Task.CompletedTask;
        }, vm.CanDeleteSelectedSessions);
        menu.Open(anchor);
    }

    private void CopySelectedSession(SessionTreeViewModel vm)
    {
        var scrollViewer = SessionTree?
            .GetVisualDescendants()
            .OfType<Avalonia.Controls.ScrollViewer>()
            .FirstOrDefault();
        var offset = scrollViewer?.Offset ?? default;

        vm.CopySelectedSession();

        // Inserting the copy should not move the grid away from the current row.
        // Restore the scroll offset in case the data source refresh resets it.
        if (scrollViewer != null)
        {
            Dispatcher.UIThread.Post(
                () => scrollViewer.Offset = offset,
                DispatcherPriority.Loaded);
        }
    }

    private void OnTreeSelectionChanged(object? sender, DataGridSelectionChangedEventArgs e)
    {
        if (_selectionSyncPending)
            return;

        _selectionSyncPending = true;
        var generation = _selectionSyncGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _selectionSyncGeneration || !IsLoaded)
            {
                _selectionSyncPending = false;
                return;
            }

            _selectionSyncPending = false;
            if (DataContext is not SessionTreeViewModel vm)
                return;

            var selectedNodes = GetSelectedNodes();
            if (!selectedNodes.SequenceEqual(vm.SelectedNodes))
                vm.SetSelectedNodes(selectedNodes);
        }, DispatcherPriority.Background);
    }

    private IReadOnlyList<SessionNodeViewModel> GetSelectedNodes()
    {
        return _gridSource?.GetSelectedItems(SessionTree)
            .Where(node => node.Session != null)
            .ToList() ?? [];
    }

    private static async System.Threading.Tasks.Task<bool> ShowLocalizedDeleteConfirmWindow(
        Avalonia.Controls.Window owner,
        IReadOnlyList<SessionInfo> sessions)
    {
        var message = sessions.Count == 1
            ? Tf("Dialog.SessionDelete.Message", sessions[0].Name)
            : Tf("Dialog.SessionDelete.BatchMessage", sessions.Count);
        return await AtomUiDialogService.ShowConfirmAsync(
            owner,
            T("Dialog.SessionDelete.Title"),
            message,
            T("Common.Delete"),
            T("Common.Cancel"));
    }

}
