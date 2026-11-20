using System.Windows;
using CncHmiDemo.ViewModels;

namespace CncHmiDemo;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }
}
