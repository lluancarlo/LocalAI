using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalAI.App.ViewModels;

namespace LocalAI.App.Views;

/// <summary>Picks an assistant's language model (<see cref="ModelPickerViewModel"/>); closes with the chosen one.</summary>
public sealed partial class ModelPickerWindow : Window
{
    public ModelPickerWindow() => InitializeComponent();

    public ModelPickerWindow(ModelPickerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close(viewModel.Result);
    }

    private async void OpenRepository_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ModelPickerViewModel { RepositoryUrl: { } url }) await Launcher.LaunchUriAsync(url);
    }
}
