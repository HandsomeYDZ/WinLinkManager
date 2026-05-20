using System.Windows;

namespace SymlinkManager.App.Views;

public partial class CreateLinkDialog : Window
{
    private readonly ViewModels.CreateLinkViewModel _vm;

    public CreateLinkDialog()
    {
        InitializeComponent();
        _vm = (ViewModels.CreateLinkViewModel)DataContext;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_vm.Confirm())
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
