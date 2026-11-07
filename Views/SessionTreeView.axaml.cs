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
using Avalonia.Media;
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
    // Matches DataGrid.MouseWheelDelta so a forwarded wheel notch scrolls the
    // same distance as it would without an active row drag.
    private const double MouseWheelDelta = 50;
    private const double RowDragThreshold = 4;
    private const double AutoScrollEdgeSize = 28;
    private const double AutoScrollMaxStep = 26;

    private static readonly MethodInfo? UpdateScrollMethod =
        typeof(DataGrid).GetMethod("UpdateScroll", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly DispatcherTimer _rowDragAutoScrollTimer;

    private bool _selectionSyncPending;
    private int _selectionSyncGeneration;
    private SessionNodeViewModel? _selectionAnchorNode;
    private DataGridSourceAdapter<SessionNodeViewModel>? _gridSource;
    private DataGridKeyLookupSource? _sessionGridSource;

    private IPointer? _rowDragPointer;
    private SessionNodeViewModel? _rowDragSourceNode;
    private Point _rowDragStartPoint;
    private Point _rowDragLastPoint;
    private bool _rowDragInProgress;
    private SessionNodeViewModel? _rowDragTargetNode;
    private bool _rowDragInsertAfter;

    private static string T(string key) => LocalizationService.Shared.Text(key);

    private static string Tf(string key, params object[] args) => string.Format(T(key), args);

    public SessionTreeView()
    {
        InitializeComponent();
        _rowDragAutoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(30),
            DispatcherPriority.Input,
            (_, _) => TickRowDragAutoScroll());
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        EndRowDrag();
        DetachGridSource();
        if (DataContext is not SessionTreeViewModel vm || SessionTree == null)
            return;

        _selectionSyncGeneration++;
        _selectionSyncPending = false;
        _gridSource = new DataGridSourceAdapter<SessionNodeViewModel>(vm.SessionRows);
        _sessionGridSource = new DataGridKeyLookupSource(_gridSource.Source);
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
            SessionTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
            SessionTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
            SessionTree.AddHandler(PointerCaptureLostEvent, OnTreePointerCaptureLost, RoutingStrategies.Tunnel);
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
        EndRowDrag();
        RemoveHandler(PointerWheelChangedEvent, OnTreePointerWheelChanged);
        if (SessionTree != null)
        {
            SessionTree.RemoveHandler(PointerPressedEvent, OnTreePointerPressed);
            SessionTree.RemoveHandler(PointerMovedEvent, OnTreePointerMoved);
            SessionTree.RemoveHandler(PointerReleasedEvent, OnTreePointerReleased);
            SessionTree.RemoveHandler(PointerCaptureLostEvent, OnTreePointerCaptureLost);
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
        if (SessionTree?.Columns.Count >= 5)
        {
            SessionTree.Columns[0].Header = LocalizationService.Shared.Text("SessionManager.ColumnName");
            SessionTree.Columns[1].Header = LocalizationService.Shared.Text("SessionManager.ColumnHost");
            SessionTree.Columns[2].Header = LocalizationService.Shared.Text("SessionManager.ColumnUsername");
            SessionTree.Columns[3].Header = LocalizationService.Shared.Text("SessionManager.ColumnProtocol");
            SessionTree.Columns[4].Header = LocalizationService.Shared.Text("SessionManager.ColumnPort");
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        if (SessionTree == null) return;

        var point = e.GetCurrentPoint(SessionTree);
        var node = FindClickedNode(e);

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

        // The session manager owns row reordering instead of AtomUI's built-in
        // session, which is cancelled whenever scrolling recycles the source
        // row. CanUserReorderRows stays false so AtomUI never takes over the
        // gesture; pressing anywhere on a row starts our own drag candidate.
        if (e.ClickCount == 1 && !IsInteractiveRowSource(e.Source))
        {
            var row = FindVisualAncestor<DataGridRow>(e.Source as Visual);
            if (row != null)
                BeginRowDragCandidate(e, row, node);
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

    private void BeginRowDragCandidate(
        PointerPressedEventArgs e,
        DataGridRow row,
        SessionNodeViewModel node)
    {
        if (SessionTree == null || node.Session == null)
            return;

        EndRowDrag();

        _rowDragPointer = e.Pointer;
        _rowDragSourceNode = node;
        _rowDragStartPoint = e.GetPosition(SessionTree);
        _rowDragLastPoint = _rowDragStartPoint;
        _rowDragTargetNode = null;
        _rowDragInsertAfter = false;
        _rowDragInProgress = false;

        e.Pointer.Capture(SessionTree);
        if (!ReferenceEquals(e.Pointer.Captured, SessionTree))
            EndRowDrag();
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (SessionTree == null ||
            _rowDragPointer == null ||
            !ReferenceEquals(e.Pointer, _rowDragPointer))
        {
            return;
        }

        var point = e.GetPosition(SessionTree);
        _rowDragLastPoint = point;

        if (!_rowDragInProgress)
        {
            if (_rowDragSourceNode == null ||
                !e.GetCurrentPoint(SessionTree).Properties.IsLeftButtonPressed)
            {
                EndRowDrag();
                return;
            }

            var deltaX = point.X - _rowDragStartPoint.X;
            var deltaY = point.Y - _rowDragStartPoint.Y;
            if (Math.Abs(deltaX) < RowDragThreshold && Math.Abs(deltaY) < RowDragThreshold)
                return;

            // Only vertical movement starts a row move; horizontal movement is
            // column resizing and must not turn into a reorder.
            if (Math.Abs(deltaX) > Math.Abs(deltaY) * 2)
                return;

            _rowDragInProgress = true;
            _rowDragAutoScrollTimer.Start();
            SessionTree.LayoutUpdated += OnRowDragLayoutUpdated;
        }

        UpdateRowDropTarget(point);
        e.Handled = true;
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_rowDragPointer == null || !ReferenceEquals(e.Pointer, _rowDragPointer))
            return;

        var wasDragging = _rowDragInProgress;
        var sourceNode = _rowDragSourceNode;
        var targetNode = _rowDragTargetNode;
        var insertAfter = _rowDragInsertAfter;

        EndRowDrag();

        if (!wasDragging || sourceNode == null || targetNode == null ||
            ReferenceEquals(sourceNode, targetNode))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is SessionTreeViewModel vm)
            vm.MoveSession(sourceNode, targetNode, insertAfter);

        e.Handled = true;
    }

    private void OnTreePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_rowDragPointer != null && ReferenceEquals(e.Pointer, _rowDragPointer))
            EndRowDrag();
    }

    private void EndRowDrag()
    {
        _rowDragAutoScrollTimer.Stop();
        if (SessionTree != null)
            SessionTree.LayoutUpdated -= OnRowDragLayoutUpdated;

        var pointer = _rowDragPointer;
        _rowDragPointer = null;
        _rowDragSourceNode = null;
        _rowDragTargetNode = null;
        _rowDragInsertAfter = false;
        _rowDragInProgress = false;
        HideRowDropIndicator();

        if (pointer != null &&
            SessionTree != null &&
            ReferenceEquals(pointer.Captured, SessionTree))
        {
            pointer.Capture(null);
        }
    }

    private void OnTreePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_rowDragInProgress)
            return;

        // The pointer is captured by this view during a row drag, so the wheel
        // event never reaches DataGrid.OnPointerWheelChanged. Apply the same
        // scroll that handler would apply; the layout update that follows
        // recomputes the insertion point.
        var scrolled = TryUpdateScroll(e.Delta * MouseWheelDelta);
        if (scrolled)
            e.Handled = true;
    }

    private void RefreshRowDropTarget()
    {
        if (!_rowDragInProgress || SessionTree == null)
            return;

        UpdateRowDropTarget(_rowDragLastPoint);
    }

    private void OnRowDragLayoutUpdated(object? sender, EventArgs e)
    {
        // Scrolling recycles and re-arranges rows, so an indicator computed
        // before the layout pass points at stale bounds. Recompute after every
        // layout while a drag is active.
        RefreshRowDropTarget();
    }

    private void TickRowDragAutoScroll()
    {
        if (!_rowDragInProgress || SessionTree == null)
        {
            _rowDragAutoScrollTimer.Stop();
            return;
        }

        if (!TryGetRowsViewport(out var top, out var bottom))
            return;

        var pointerY = _rowDragLastPoint.Y;
        var delta = 0d;
        if (pointerY < top + AutoScrollEdgeSize)
        {
            var ratio = Math.Clamp(
                (top + AutoScrollEdgeSize - pointerY) / AutoScrollEdgeSize,
                0,
                1);
            delta = ratio * AutoScrollMaxStep;
        }
        else if (pointerY > bottom - AutoScrollEdgeSize)
        {
            var ratio = Math.Clamp(
                (pointerY - (bottom - AutoScrollEdgeSize)) / AutoScrollEdgeSize,
                0,
                1);
            delta = -ratio * AutoScrollMaxStep;
        }

        if (Math.Abs(delta) < 0.5)
            return;

        // The resulting layout update recomputes the insertion point.
        TryUpdateScroll(new Vector(0, delta));
    }

    private bool TryGetRowsViewport(out double top, out double bottom)
    {
        top = 0;
        bottom = 0;
        if (SessionTree == null || FindRowsPresenter() is not { } presenter)
            return false;

        var origin = presenter.TranslatePoint(default, SessionTree);
        if (origin is not { } position || presenter.Bounds.Height <= 0)
            return false;

        top = position.Y;
        bottom = top + presenter.Bounds.Height;
        return true;
    }

    private DataGridRowsPresenter? FindRowsPresenter() =>
        SessionTree?.GetVisualDescendants()
            .OfType<DataGridRowsPresenter>()
            .FirstOrDefault();

    private bool TryUpdateScroll(Vector delta)
    {
        if (UpdateScrollMethod == null || SessionTree == null)
            return false;

        try
        {
            return UpdateScrollMethod.Invoke(SessionTree, [delta]) is true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    private void UpdateRowDropTarget(Point point)
    {
        var presenter = FindRowsPresenter();
        if (presenter == null || SessionTree == null)
        {
            HideRowDropIndicator();
            return;
        }

        var origin = presenter.TranslatePoint(default, SessionTree);
        if (origin is not { } presenterOrigin)
        {
            HideRowDropIndicator();
            return;
        }

        var rows = presenter.Children
            .OfType<DataGridRow>()
            .Where(row => row.Index >= 0 &&
                          row.DataContext is SessionNodeViewModel { Session: not null })
            .OrderBy(row => row.Index)
            .ToList();
        if (rows.Count == 0)
        {
            HideRowDropIndicator();
            return;
        }

        var presenterTop = presenterOrigin.Y;
        var presenterBottom = presenterTop + presenter.Bounds.Height;
        var pointerY = Math.Clamp(point.Y, presenterTop, presenterBottom);

        SessionNodeViewModel? targetNode = null;
        var insertAfter = false;
        var indicatorY = presenterTop;

        foreach (var row in rows)
        {
            var rowTop = presenterTop + row.Bounds.Y;
            var rowBottom = rowTop + row.Bounds.Height;
            var node = (SessionNodeViewModel)row.DataContext!;

            if (pointerY < rowTop)
            {
                targetNode = node;
                insertAfter = false;
                indicatorY = rowTop;
                break;
            }

            targetNode = node;
            if (pointerY < rowBottom)
            {
                insertAfter = pointerY >= rowTop + row.Bounds.Height / 2;
                indicatorY = insertAfter ? rowBottom : rowTop;
                break;
            }

            insertAfter = true;
            indicatorY = rowBottom;
        }

        _rowDragTargetNode = targetNode;
        _rowDragInsertAfter = insertAfter;
        ShowRowDropIndicator(indicatorY);
    }

    private void ShowRowDropIndicator(double y)
    {
        if (RowDropIndicator == null)
            return;

        RowDropIndicator.IsVisible = true;
        if (RowDropIndicator.RenderTransform is TranslateTransform transform)
        {
            transform.Y = y;
        }
        else
        {
            RowDropIndicator.RenderTransform = new TranslateTransform(0, y);
        }
    }

    private void HideRowDropIndicator()
    {
        if (RowDropIndicator != null)
            RowDropIndicator.IsVisible = false;
    }

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
