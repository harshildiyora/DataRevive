using System.ComponentModel;
using System.Windows;
using DataRecovery.App.ViewModels;

namespace DataRecovery.App
{
    public partial class MainWindow : Window
    {
        private GridLength _lastLeftWidth = new GridLength(280);
        private GridLength _lastRightWidth = new GridLength(380);

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dispose();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += ViewModel_PropertyChanged;
                UpdateLeftPaneLayout(vm.IsLeftPaneVisible);
                UpdateRightPaneLayout(vm.IsRightPaneVisible);
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MainViewModel vm) return;

            if (e.PropertyName == nameof(MainViewModel.IsLeftPaneVisible))
            {
                UpdateLeftPaneLayout(vm.IsLeftPaneVisible);
            }
            else if (e.PropertyName == nameof(MainViewModel.IsRightPaneVisible))
            {
                UpdateRightPaneLayout(vm.IsRightPaneVisible);
            }
        }

        private void UpdateLeftPaneLayout(bool isVisible)
        {
            if (LeftCol == null || LeftFolderBorder == null || LeftSplitter == null || LeftSplitterCol == null) return;

            if (!isVisible)
            {
                if (LeftCol.ActualWidth > 50)
                {
                    _lastLeftWidth = new GridLength(LeftCol.ActualWidth);
                }
                LeftCol.MinWidth = 0;
                LeftCol.Width = new GridLength(0);
                LeftSplitterCol.Width = new GridLength(0);
                LeftFolderBorder.Visibility = Visibility.Collapsed;
                LeftSplitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                LeftFolderBorder.Visibility = Visibility.Visible;
                LeftSplitter.Visibility = Visibility.Visible;
                LeftSplitterCol.Width = new GridLength(6);
                LeftCol.MinWidth = 180;
                LeftCol.Width = _lastLeftWidth.Value > 100 ? _lastLeftWidth : new GridLength(280);
            }
        }

        private void UpdateRightPaneLayout(bool isVisible)
        {
            if (RightCol == null || RightInspectorBorder == null || RightSplitter == null || RightSplitterCol == null) return;

            if (!isVisible)
            {
                if (RightCol.ActualWidth > 50)
                {
                    _lastRightWidth = new GridLength(RightCol.ActualWidth);
                }
                RightCol.MinWidth = 0;
                RightCol.Width = new GridLength(0);
                RightSplitterCol.Width = new GridLength(0);
                RightInspectorBorder.Visibility = Visibility.Collapsed;
                RightSplitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                RightInspectorBorder.Visibility = Visibility.Visible;
                RightSplitter.Visibility = Visibility.Visible;
                RightSplitterCol.Width = new GridLength(6);
                RightCol.MinWidth = 240;
                RightCol.Width = _lastRightWidth.Value > 100 ? _lastRightWidth : new GridLength(380);
            }
        }

        private void CloseFilterPopups_Click(object sender, RoutedEventArgs e)
        {
            if (TypeFilterBtn != null) TypeFilterBtn.IsChecked = false;
            if (DateFilterBtn != null) DateFilterBtn.IsChecked = false;
            if (SizeFilterBtn != null) SizeFilterBtn.IsChecked = false;
        }

        private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is ViewModels.MainViewModel vm && e.NewValue is Models.FolderTreeNode node)
            {
                vm.SelectedFolderNode = node;
            }
        }
    }
}
