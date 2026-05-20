using System.Windows;
using SymlinkManager.App.ViewModels;

namespace SymlinkManager.App;

public partial class MainWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
