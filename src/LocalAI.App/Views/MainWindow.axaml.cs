using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LocalAI.App.ViewModels;

namespace LocalAI.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _pttKeyDown;
    private bool _stickToBottom = true;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel so Enter is handled before the multi-line TextBox inserts a newline.
        InputBox.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel);

        // Button marks pointer events handled; listen to handled events too for press-and-hold.
        PushToTalkButton.AddHandler(PointerPressedEvent, PushToTalk_Pressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PushToTalkButton.AddHandler(PointerReleasedEvent, PushToTalk_Released, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PushToTalkButton.AddHandler(PointerCaptureLostEvent, (_, _) => _ = Vm?.EndPushToTalkAsync(), handledEventsToo: true);

        MessagesScroll.ScrollChanged += (_, _) =>
        {
            // Auto-scroll only while the user is at (or near) the bottom.
            var distance = MessagesScroll.Extent.Height - MessagesScroll.Viewport.Height - MessagesScroll.Offset.Y;
            _stickToBottom = distance < 60;
        };

        DataContextChanged += (_, _) =>
        {
            if (Vm == null) return;
            Vm.ScrollToEndRequested += (_, _) => ScrollToEnd();
            // Focus Cancel when the delete confirmation opens, so Enter never deletes by accident.
            Vm.Settings.Assistants.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AssistantsViewModel.PendingDelete) && Vm.Settings.Assistants.PendingDelete != null)
                    Dispatcher.UIThread.Post(() => CancelDeleteButton.Focus(), DispatcherPriority.Loaded);
            };
        };
        Opened += (_, _) => InputBox.Focus();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void ScrollToEnd()
    {
        if (!_stickToBottom) return;
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _stickToBottom = true;
            if (Vm?.SendCommand.CanExecute(null) == true && Vm.IsBusy == false) Vm.SendCommand.Execute(null);
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null || Vm.ShowWelcome) return;
        if (Vm.Settings.Assistants.PendingDelete is { } pendingDelete)
        {
            // The confirmation popup is modal: Escape cancels it and no other shortcut runs behind it.
            if (e.Key == Key.Escape) pendingDelete.CancelCommand.Execute(null);
            if (e.Key is not (Key.Tab or Key.Enter or Key.Space)) e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Vm.StopCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.N when e.KeyModifiers == KeyModifiers.Control:
                Vm.NewConversationCommand.Execute(null);
                InputBox.Focus();
                e.Handled = true;
                break;
            case Key.F2:
                Vm.BeginRenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                if (!_pttKeyDown) // ignore key repeat
                {
                    _pttKeyDown = true;
                    _ = Vm.BeginPushToTalkAsync();
                }
                break;
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (_pttKeyDown && (e.Key == Key.Space || e.Key is Key.LeftCtrl or Key.RightCtrl))
        {
            _pttKeyDown = false;
            e.Handled = true;
            _ = Vm?.EndPushToTalkAsync();
        }
    }

    private void PushToTalk_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PushToTalkButton).Properties.IsLeftButtonPressed) _ = Vm?.BeginPushToTalkAsync();
    }

    private void PushToTalk_Released(object? sender, PointerReleasedEventArgs e) => _ = Vm?.EndPushToTalkAsync();

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: ConversationItemViewModel item } || Vm == null) return;
        if (e.Key == Key.Enter)
        {
            Vm.CommitRenameCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.CancelRenameCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConversationItemViewModel item }) Vm?.CommitRenameCommand.Execute(item);
    }
}
