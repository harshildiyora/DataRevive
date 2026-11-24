using System;
using System.Windows;
using DataRecovery.Core.Security;

namespace DataRecovery.App.Views
{
    public partial class ConflictDialog : Window
    {
        public ConflictChoice SelectedChoice { get; private set; } = ConflictChoice.KeepBothRename;
        public bool ApplyToAll => ChkApplyToAll.IsChecked == true;

        public ConflictDialog(ConflictResolutionRequest request)
        {
            InitializeComponent();

            TxtSummary.Text = $"The file '{request.FileName}' already exists in destination folder:\n{request.TargetDirectory}";
            TxtExistingName.Text = request.FileName;
            TxtExistingSize.Text = request.ExistingSizeDisplay;
            TxtExistingDate.Text = request.ExistingFileDate.ToString("yyyy-MM-dd HH:mm:ss");

            TxtIncomingName.Text = request.FileName;
            TxtIncomingSize.Text = request.IncomingSizeDisplay;
            TxtIncomingDate.Text = $"{request.IncomingStatus} • {request.IncomingFileDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown"}";
        }

        private void BtnKeepBoth_Click(object sender, RoutedEventArgs e)
        {
            SelectedChoice = ConflictChoice.KeepBothRename;
            DialogResult = true;
            Close();
        }

        private void BtnOverwrite_Click(object sender, RoutedEventArgs e)
        {
            SelectedChoice = ConflictChoice.Overwrite;
            DialogResult = true;
            Close();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            SelectedChoice = ConflictChoice.Skip;
            DialogResult = true;
            Close();
        }
    }
}
