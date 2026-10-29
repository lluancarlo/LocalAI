using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalAI.App.ViewModels;

namespace LocalAI.App.Views;

public sealed partial class NewAssistantForm : UserControl
{
    public NewAssistantForm() => InitializeComponent();

    /// <summary>Opens the model dialog; the model chosen there becomes the new assistant's.</summary>
    private async void ChooseModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistantsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var chosen = await new ModelPickerWindow(vm.CreateModelPicker()).ShowDialog<ChosenModel?>(owner);
        if (chosen != null) vm.UseModel(chosen);
    }
}
