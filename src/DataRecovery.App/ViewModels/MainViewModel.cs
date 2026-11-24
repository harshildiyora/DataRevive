using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRecovery.App.Models;
using DataRecovery.App.Views;
using DataRecovery.Core.Engine;
using DataRecovery.Core.Security;
using DataRecovery.Core.Signatures;
using DataRecovery.Core.Storage;
using Microsoft.Win32;

namespace DataRecovery.App.ViewModels
{
    public enum AppViewMode
    {
        DriveSelection,
        ScanResults
    }

    public partial class MainViewModel : ObservableObject
    {
        private CancellationTokenSource? _scanCts;
        private ISectorReader? _activeReader;
        private readonly FileExporter _exporter = new();
        private ICollectionView? _filesView;

        [ObservableProperty]
        private AppViewMode _currentView = AppViewMode.DriveSelection;

        [ObservableProperty]
        private bool _isDarkMode = true;

        public string ThemeIcon => IsDarkMode ? "🌙" : "☀️";
        public string ThemeText => IsDarkMode ? "Dark" : "Light";

        [ObservableProperty]
        private ObservableCollection<StorageDevice> _devices = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedDrive))]
        private StorageDevice? _selectedDevice;

        public StorageDevice? SelectedDrive => SelectedDevice;

        [ObservableProperty]
        private string _destinationPath = string.Empty;

        [ObservableProperty]
        private string _statusMessage = "Ready. Select a target drive to search for lost data.";

        [ObservableProperty]
        private string _securityNotice = "Software Write-Blocking Active (GENERIC_READ Only)";

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        private double _scanPercentage;

        [ObservableProperty]
        private string _progressText = "0% (0 B / 0 B)";

        [ObservableProperty]
        private string _scanSpeedText = "0.0 MB/s";

        [ObservableProperty]
        private string _etaText = "--:--";

        [ObservableProperty]
        private int _totalFilesFound;

        [ObservableProperty]
        private ObservableCollection<CarvedFileResult> _discoveredFiles = new();

        [ObservableProperty]
        private CarvedFileResult? _selectedFile;

        [ObservableProperty]
        private ImageSource? _previewImage;

        [ObservableProperty]
        private string _previewImageDimensions = string.Empty;

        [ObservableProperty]
        private string _previewErrorMessage = string.Empty;

        [ObservableProperty]
        private int _selectedPreviewTabIndex;

        [ObservableProperty]
        private string _previewText = string.Empty;

        [ObservableProperty]
        private string _previewHex = string.Empty;

        [ObservableProperty]
        private string _selectedCategoryFilter = "All";

        [ObservableProperty]
        private string _selectedExtensionFilter = "All";

        [ObservableProperty]
        private string _dateFilterPreset = "All"; // All, Today, Past7Days, Past30Days, ThisYear, Custom

        [ObservableProperty]
        private DateTime? _dateFilterStart;

        [ObservableProperty]
        private DateTime? _dateFilterEnd;

        [ObservableProperty]
        private string _sizeFilterPreset = "All"; // All, Tiny, Small, Medium, Large, Huge

        [ObservableProperty]
        private bool _hasActiveFilters;

        [ObservableProperty]
        private bool _isLeftPaneVisible = true;

        [ObservableProperty]
        private bool _isRightPaneVisible = true;

        public string LeftPaneToggleIcon => IsLeftPaneVisible ? "📁 Hide Tree ◀" : "📁 Show Tree ▶";
        public string RightPaneToggleIcon => IsRightPaneVisible ? "▶ Hide Preview" : "◀ Show Preview";

        partial void OnIsLeftPaneVisibleChanged(bool value)
        {
            OnPropertyChanged(nameof(LeftPaneToggleIcon));
        }

        partial void OnIsRightPaneVisibleChanged(bool value)
        {
            OnPropertyChanged(nameof(RightPaneToggleIcon));
        }

        [ObservableProperty]
        private ObservableCollection<FolderTreeNode> _rootFolderNodes = new();

        [ObservableProperty]
        private FolderTreeNode? _selectedFolderNode;

        [ObservableProperty]
        private string _currentBreadcrumbPath = "Drive";

        private HashSet<CarvedFileResult>? _selectedFolderFilesSet;

        [ObservableProperty]
        private ObservableCollection<string> _availableExtensions = new() { "All" };

        [ObservableProperty]
        private string _searchFilterText = string.Empty;

        // Category counts for DiskGenius style tree view
        [ObservableProperty] private int _countAll;
        [ObservableProperty] private int _countDeleted;
        [ObservableProperty] private int _countRecycleBin;
        [ObservableProperty] private int _countDocuments;
        [ObservableProperty] private int _countImages;
        [ObservableProperty] private int _countVideo;
        [ObservableProperty] private int _countAudio;
        [ObservableProperty] private int _countArchives;

        public ICollectionView FilteredFiles
        {
            get
            {
                if (_filesView == null)
                {
                    _filesView = CollectionViewSource.GetDefaultView(DiscoveredFiles);
                    _filesView.Filter = FilterFileItem;
                }
                return _filesView;
            }
        }

        public string FilteredFilesCountText
        {
            get
            {
                int total = DiscoveredFiles.Count;
                int count = 0;
                if (_filesView != null)
                {
                    foreach (var _ in _filesView) count++;
                }
                return count == total ? $"{total} items" : $"{count} of {total} items";
            }
        }

        public MainViewModel()
        {
            RefreshDevices();
            DestinationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RecoveredFiles");
            _filesView = CollectionViewSource.GetDefaultView(DiscoveredFiles);
            _filesView.Filter = FilterFileItem;
        }

        partial void OnSelectedFolderNodeChanged(FolderTreeNode? value)
        {
            SelectedFile = null; // Clear file selection so folder/drive metadata is displayed!

            if (value != null && !value.IsRoot)
            {
                _selectedFolderFilesSet = new HashSet<CarvedFileResult>(value.GetAllFilesRecursive());
                CurrentBreadcrumbPath = $"{SelectedDevice?.DisplayName ?? "Drive"} > {value.FullPath.Trim('\\').Replace("\\", " > ")}";
            }
            else
            {
                _selectedFolderFilesSet = null;
                CurrentBreadcrumbPath = SelectedDevice?.DisplayName ?? "Root Directory";
            }

            UpdateActiveFilterFlag();
            _filesView?.Refresh();
            OnPropertyChanged(nameof(FilteredFilesCountText));
        }

        private bool FilterFileItem(object obj)
        {
            if (obj is not CarvedFileResult item) return false;

            // 0. Folder Tree Filter: If a subfolder is selected
            if (_selectedFolderFilesSet != null && !_selectedFolderFilesSet.Contains(item))
            {
                return false;
            }

            // 1. Search text filter
            if (!string.IsNullOrWhiteSpace(SearchFilterText))
            {
                string query = SearchFilterText.Trim();
                bool matchesName = !string.IsNullOrEmpty(item.OriginalFileName) && item.OriginalFileName.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchesFallback = !string.IsNullOrEmpty(item.FileName) && item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchesExt = !string.IsNullOrEmpty(item.Extension) && item.Extension.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matchesName && !matchesFallback && !matchesExt) return false;
            }

            // 2. Specific Extension filter
            if (!string.IsNullOrEmpty(SelectedExtensionFilter) && !SelectedExtensionFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (!item.Extension.Equals(SelectedExtensionFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // 3. Category filter
            if (!string.IsNullOrEmpty(SelectedCategoryFilter) && !SelectedCategoryFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                bool catMatch = SelectedCategoryFilter switch
                {
                    "Deleted" => item.Status.Contains("Deleted", StringComparison.OrdinalIgnoreCase),
                    "RecycleBin" => item.Status.Contains("Recycle", StringComparison.OrdinalIgnoreCase),
                    "Documents" => item.Category == FileCategory.Documents,
                    "Images" => item.Category == FileCategory.Images,
                    "Video" => item.Category == FileCategory.Video,
                    "Audio" => item.Category == FileCategory.Audio,
                    "Archives" => item.Category == FileCategory.Archives,
                    _ => true
                };
                if (!catMatch) return false;
            }

            // 4. Windows File Explorer Date filter
            if (!string.IsNullOrEmpty(DateFilterPreset) && !DateFilterPreset.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                DateTime itemDate = item.DeletedDate ?? item.DiscoveredTime;
                DateTime now = DateTime.Now;

                bool dateMatch = DateFilterPreset switch
                {
                    "Today" => itemDate.Date == now.Date,
                    "Past7Days" => itemDate >= now.AddDays(-7),
                    "Past30Days" => itemDate >= now.AddDays(-30),
                    "ThisYear" => itemDate.Year == now.Year,
                    "Custom" => (!DateFilterStart.HasValue || itemDate.Date >= DateFilterStart.Value.Date) &&
                                (!DateFilterEnd.HasValue || itemDate.Date <= DateFilterEnd.Value.Date),
                    _ => true
                };
                if (!dateMatch) return false;
            }

            // 5. Windows File Explorer Size filter
            if (!string.IsNullOrEmpty(SizeFilterPreset) && !SizeFilterPreset.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                long bytes = item.FileSizeBytes;
                bool sizeMatch = SizeFilterPreset switch
                {
                    "Tiny" => bytes <= 10 * 1024,                                       // 0 - 10 KB
                    "Small" => bytes > 10 * 1024 && bytes <= 1024 * 1024,                // 10 KB - 1 MB
                    "Medium" => bytes > 1024 * 1024 && bytes <= 16 * 1024 * 1024,       // 1 MB - 16 MB
                    "Large" => bytes > 16 * 1024 * 1024 && bytes <= 128 * 1024 * 1024,  // 16 MB - 128 MB
                    "Huge" => bytes > 128 * 1024 * 1024,                                // > 128 MB
                    _ => true
                };
                if (!sizeMatch) return false;
            }

            return true;
        }

        partial void OnSearchFilterTextChanged(string value)
        {
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
        }

        partial void OnSelectedCategoryFilterChanged(string value)
        {
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
        }

        partial void OnSelectedExtensionFilterChanged(string value)
        {
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
        }

        partial void OnDateFilterPresetChanged(string value)
        {
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
        }

        partial void OnSizeFilterPresetChanged(string value)
        {
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
        }

        partial void OnDateFilterStartChanged(DateTime? value)
        {
            if (DateFilterPreset == "Custom")
            {
                UpdateActiveFilterFlag();
                _filesView?.Refresh();
            }
        }

        partial void OnDateFilterEndChanged(DateTime? value)
        {
            if (DateFilterPreset == "Custom")
            {
                UpdateActiveFilterFlag();
                _filesView?.Refresh();
            }
        }

        [RelayCommand]
        public void SetDatePreset(string preset)
        {
            DateFilterPreset = preset;
        }

        [RelayCommand]
        public void SetSizePreset(string preset)
        {
            SizeFilterPreset = preset;
        }

        [RelayCommand]
        public void SetExtensionFilter(string ext)
        {
            SelectedExtensionFilter = ext;
        }

        [RelayCommand]
        public void ApplyCustomDateRange()
        {
            DateFilterPreset = "Custom";
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
        }

        [RelayCommand]
        public void ClearAllFilters()
        {
            SearchFilterText = string.Empty;
            SelectedCategoryFilter = "All";
            SelectedExtensionFilter = "All";
            DateFilterPreset = "All";
            DateFilterStart = null;
            DateFilterEnd = null;
            SizeFilterPreset = "All";
            if (RootFolderNodes.Count > 0)
            {
                SelectedFolderNode = RootFolderNodes[0];
            }
            UpdateActiveFilterFlag();
            _filesView?.Refresh();
            OnPropertyChanged(nameof(FilteredFilesCountText));
        }

        private void UpdateActiveFilterFlag()
        {
            HasActiveFilters = !string.IsNullOrWhiteSpace(SearchFilterText) ||
                               SelectedCategoryFilter != "All" ||
                               SelectedExtensionFilter != "All" ||
                               DateFilterPreset != "All" ||
                               SizeFilterPreset != "All" ||
                               (SelectedFolderNode != null && !SelectedFolderNode.IsRoot);
        }

        [RelayCommand]
        public void ToggleLeftPane()
        {
            IsLeftPaneVisible = !IsLeftPaneVisible;
        }

        [RelayCommand]
        public void ToggleRightPane()
        {
            IsRightPaneVisible = !IsRightPaneVisible;
        }

        [RelayCommand]
        public void NavigateUpFolder()
        {
            if (SelectedFolderNode?.Parent != null)
            {
                SelectedFolderNode = SelectedFolderNode.Parent;
            }
            else if (RootFolderNodes.Count > 0)
            {
                SelectedFolderNode = RootFolderNodes[0];
            }
        }

        [RelayCommand]
        public void SelectRootFolder()
        {
            if (RootFolderNodes.Count > 0)
            {
                SelectedFolderNode = RootFolderNodes[0];
            }
        }

        [RelayCommand]
        public void ExpandAllFolders()
        {
            foreach (var node in RootFolderNodes)
            {
                SetExpandedRecursive(node, true);
            }
        }

        [RelayCommand]
        public void CollapseAllFolders()
        {
            foreach (var node in RootFolderNodes)
            {
                SetExpandedRecursive(node, false);
                node.IsExpanded = true;
            }
        }

        private void SetExpandedRecursive(FolderTreeNode node, bool expanded)
        {
            node.IsExpanded = expanded;
            foreach (var child in node.Children)
            {
                SetExpandedRecursive(child, expanded);
            }
        }

        [RelayCommand]
        public async Task ExportFolderAsync(FolderTreeNode? folder)
        {
            var targetFolder = folder ?? SelectedFolderNode;
            if (targetFolder == null)
            {
                MessageBox.Show("Please select a folder in the tree to export.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var filesToExport = targetFolder.GetAllFilesRecursive().ToList();
            if (filesToExport.Count == 0)
            {
                MessageBox.Show($"Folder '{targetFolder.Name}' contains no files to export.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_activeReader == null || SelectedDevice == null) return;

            var dlg = new OpenFolderDialog
            {
                Title = $"Select Destination for Exporting Folder '{targetFolder.Name}'",
                InitialDirectory = Directory.Exists(DestinationPath) ? DestinationPath : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dlg.ShowDialog() != true) return;

            string targetDest = dlg.FolderName;

            if (!SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, targetDest, out string securityErr))
            {
                MessageBox.Show(securityErr + "\n\nPlease choose an export destination on another drive to avoid overwriting lost data.", "Anti-Overwrite Protection", MessageBoxButton.OK, MessageBoxImage.Hand);
                return;
            }

            _exporter.ResetConflictMemory();

            int count = 0;
            StatusMessage = $"Exporting folder '{targetFolder.Name}' ({filesToExport.Count} files)...";

            foreach (var file in filesToExport)
            {
                try
                {
                    string? path = await _exporter.ExportFileAsync(
                        _activeReader,
                        file,
                        targetDest,
                        CancellationToken.None,
                        ResolveConflictAsync,
                        preserveFolderStructure: true);

                    if (path != null) count++;
                }
                catch { }
            }

            await _exporter.WriteManifestAsync(targetDest, filesToExport.Where(f => f.IsExported));
            StatusMessage = $"Export complete. Successfully recovered {count} files from '{targetFolder.Name}'.";
            MessageBox.Show($"Successfully exported {count} files from folder '{targetFolder.Name}' to:\n{targetDest}\n\nExact directory hierarchy preserved.", "Folder Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddFileToFolderTree(CarvedFileResult file)
        {
            if (RootFolderNodes.Count == 0) return;
            var root = RootFolderNodes[0];

            string folderPath = "";
            if (!string.IsNullOrWhiteSpace(file.OriginalPath))
            {
                folderPath = Path.GetDirectoryName(file.OriginalPath) ?? "";
            }

            if (string.IsNullOrWhiteSpace(folderPath) || folderPath == "\\" || folderPath == "/")
            {
                if (file.Status.Contains("Carved", StringComparison.OrdinalIgnoreCase))
                {
                    folderPath = Path.Combine("Raw Carved Files", file.Category.ToString(), file.Extension.ToUpperInvariant());
                }
                else
                {
                    folderPath = "Root Directory";
                }
            }

            folderPath = folderPath.TrimStart('\\', '/');
            if (folderPath.Length >= 2 && folderPath[1] == ':')
            {
                folderPath = folderPath.Substring(2).TrimStart('\\', '/');
            }

            var targetFolder = root.GetOrCreateSubfolder(folderPath);
            targetFolder.Files.Add(file);
        }

        [RelayCommand]
        public void SetCategoryFilter(string category)
        {
            SelectedCategoryFilter = category;
        }

        [RelayCommand]
        public void ToggleTheme()
        {
            IsDarkMode = !IsDarkMode;
            DataRecovery.App.Services.ThemeService.ApplyTheme(IsDarkMode);
            OnPropertyChanged(nameof(ThemeIcon));
            OnPropertyChanged(nameof(ThemeText));
        }

        [RelayCommand]
        public void SwitchToSelectionView()
        {
            CurrentView = AppViewMode.DriveSelection;
        }

        [RelayCommand]
        public void SwitchToResultsView()
        {
            CurrentView = AppViewMode.ScanResults;
        }

        [RelayCommand]
        public void RefreshDevices()
        {
            Devices.Clear();
            var list = DeviceEnumerator.GetAvailableDevices();
            foreach (var d in list)
            {
                Devices.Add(d);
            }
            if (Devices.Count > 0)
            {
                SelectDevice(Devices[0]);
            }
        }

        [RelayCommand]
        public void SelectDevice(StorageDevice device)
        {
            SelectedDevice = device;
        }

        partial void OnSelectedDeviceChanged(StorageDevice? oldValue, StorageDevice? newValue)
        {
            if (oldValue != null) oldValue.IsSelected = false;
            if (newValue != null)
            {
                newValue.IsSelected = true;
                EnsureSafeDestinationForDevice(newValue);
            }
        }

        private void EnsureSafeDestinationForDevice(StorageDevice? device)
        {
            if (device == null) return;
            if (!SecurityValidator.ValidateDestinationIsSafe(device, DestinationPath, out _))
            {
                var altDevice = Devices.FirstOrDefault(d => d != device && !string.IsNullOrEmpty(d.DriveLetter) && SecurityValidator.ValidateDestinationIsSafe(device, d.DriveLetter + "\\RecoveredFiles", out _));
                if (altDevice != null)
                {
                    DestinationPath = Path.Combine(altDevice.DriveLetter + "\\", "RecoveredFiles");
                    StatusMessage = $"Export destination automatically set to {DestinationPath} to protect source drive.";
                }
            }
        }

        [RelayCommand]
        public void QuickSelectRecycleBin()
        {
            if (SelectedDevice != null)
            {
                StatusMessage = "Targeted location: Recycle Bin. Click 'Search for Lost Data' to scan.";
                StartScanCommand.Execute(null);
            }
        }

        [RelayCommand]
        public void QuickSelectDesktop()
        {
            if (SelectedDevice != null)
            {
                StatusMessage = "Targeted location: Desktop folder.";
                StartScanCommand.Execute(null);
            }
        }

        [RelayCommand]
        public void BrowseDiskImage()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Raw Disk Image or Backup File",
                Filter = "Disk Image Files (*.img;*.raw;*.dd;*.bin;*.vhd)|*.img;*.raw;*.dd;*.bin;*.vhd|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var imgDevice = DeviceEnumerator.CreateFromImageFile(dlg.FileName);
                    Devices.Add(imgDevice);
                    SelectDevice(imgDevice);
                    StatusMessage = $"Loaded disk image: {imgDevice.Name}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        public void BrowseDestination()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Safe Recovery Destination Folder"
            };

            if (dlg.ShowDialog() == true)
            {
                DestinationPath = dlg.FolderName;
            }
        }

        [RelayCommand]
        public async Task StartScanAsync()
        {
            if (SelectedDevice == null)
            {
                MessageBox.Show("Please select a target storage drive or location.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Anti-Overwrite Security Check for Destination Path
            // Scanning is strictly read-only, so scanning the source drive is 100% safe.
            // If the current destination is on the source drive, automatically redirect it to a safe drive if possible.
            if (!SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, DestinationPath, out _))
            {
                var altDevice = Devices.FirstOrDefault(d => d != SelectedDevice && !string.IsNullOrEmpty(d.DriveLetter) && SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, d.DriveLetter + "\\RecoveredFiles", out _));
                if (altDevice != null)
                {
                    DestinationPath = Path.Combine(altDevice.DriveLetter + "\\", "RecoveredFiles");
                    StatusMessage = $"Export destination automatically set to {DestinationPath} (Safe Drive).";
                }
                else
                {
                    StatusMessage = "Scanning in Read-Only Mode. Please select an external drive when exporting files.";
                }
            }

            // Check Administrator privileges for raw logical drives
            if (SelectedDevice.DeviceType != StorageDeviceType.DiskImageFile && !IsRunningAsAdministrator())
            {
                var elevateChoice = MessageBox.Show(
                    $"Direct sector access on drive {SelectedDevice.Name} requires Administrator privileges to recover deleted files.\n\nWould you like to restart DataRecovery Pro as Administrator?",
                    "Administrator Elevation Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (elevateChoice == MessageBoxResult.Yes)
                {
                    RestartAsAdministrator();
                }
                return;
            }

            // Switch to Scan / Results Screen
            CurrentView = AppViewMode.ScanResults;
            IsScanning = true;
            DiscoveredFiles.Clear();
            AvailableExtensions.Clear();
            AvailableExtensions.Add("All");
            TotalFilesFound = 0;
            ScanPercentage = 0;
            _scanCts = new CancellationTokenSource();

            // Initialize Folder Tree Root
            RootFolderNodes.Clear();
            string rootName = SelectedDevice != null ? SelectedDevice.DisplayName : "Target Storage";
            var rootNode = new FolderTreeNode(rootName, "\\", "💾");
            rootNode.IsExpanded = true;
            RootFolderNodes.Add(rootNode);
            SelectedFolderNode = rootNode;
            _selectedFolderFilesSet = null;
            CurrentBreadcrumbPath = rootName;

            // Reset category counters
            CountAll = 0;
            CountDeleted = 0;
            CountRecycleBin = 0;
            CountDocuments = 0;
            CountImages = 0;
            CountVideo = 0;
            CountAudio = 0;
            CountArchives = 0;

            try
            {
                var targetDevice = SelectedDevice;
                if (targetDevice == null) return;
                var token = _scanCts.Token;

                // 1. Scan Windows Recycle Bin items immediately for fast instant results
                if (!string.IsNullOrEmpty(targetDevice.DriveLetter))
                {
                    var binFiles = RecycleBinScanner.ScanRecycleBinForDrive(targetDevice.DriveLetter);
                    foreach (var bf in binFiles)
                    {
                        DiscoveredFiles.Add(bf);
                        UpdateCategoryCounts(bf);
                        AddFileToFolderTree(bf);
                    }
                    rootNode.NotifyAllTotalsChanged();
                    TotalFilesFound = DiscoveredFiles.Count;
                    OnPropertyChanged(nameof(FilteredFilesCountText));
                }

                // 2. Offload sector carving & MFT scan completely to background thread pool
                await Task.Run(async () =>
                {
                    _activeReader = new UnbufferedSectorReader(targetDevice);
                    var engine = new CarvingEngine();
                    var channel = Channel.CreateBounded<CarvedFileResult>(new BoundedChannelOptions(10000)
                    {
                        FullMode = BoundedChannelFullMode.Wait
                    });

                    var progressReporter = new Progress<RecoveryProgress>(p =>
                    {
                        ScanPercentage = p.Percentage;
                        ProgressText = p.DisplayProgress;
                        ScanSpeedText = p.DisplaySpeed;
                        EtaText = p.EstimatedTimeRemaining.ToString(@"mm\:ss");
                        StatusMessage = p.StatusMessage;
                    });

                    // Background batch consumer (flushes to UI without blocking Dispatcher)
                    var consumerTask = Task.Run(async () =>
                    {
                        var batch = new System.Collections.Generic.List<CarvedFileResult>(100);
                        var flushTimer = Stopwatch.StartNew();

                        while (await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                        {
                            while (channel.Reader.TryRead(out var item))
                            {
                                batch.Add(item);

                                if (batch.Count >= 50 || flushTimer.ElapsedMilliseconds >= 250)
                                {
                                    var toAdd = batch.ToArray();
                                    batch.Clear();
                                    flushTimer.Restart();

                                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                                    {
                                        foreach (var f in toAdd)
                                        {
                                            DiscoveredFiles.Add(f);
                                            UpdateCategoryCounts(f);
                                            AddFileToFolderTree(f);
                                        }
                                        rootNode.NotifyAllTotalsChanged();
                                        TotalFilesFound = DiscoveredFiles.Count;
                                        OnPropertyChanged(nameof(FilteredFilesCountText));
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }
                            }
                        }

                        if (batch.Count > 0)
                        {
                            var toAdd = batch.ToArray();
                            batch.Clear();
                            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                            {
                                foreach (var f in toAdd)
                                {
                                    DiscoveredFiles.Add(f);
                                    UpdateCategoryCounts(f);
                                    AddFileToFolderTree(f);
                                }
                                rootNode.NotifyAllTotalsChanged();
                                TotalFilesFound = DiscoveredFiles.Count;
                                OnPropertyChanged(nameof(FilteredFilesCountText));
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }, token);

                    // Background producer
                    await engine.StartCarvingAsync(_activeReader, channel.Writer, progressReporter, token).ConfigureAwait(false);
                    await consumerTask.ConfigureAwait(false);
                }, token);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    rootNode.NotifyAllTotalsChanged();
                    _filesView?.Refresh();
                    OnPropertyChanged(nameof(FilteredFilesCountText));
                });

                StatusMessage = $"Scan complete! Discovered {DiscoveredFiles.Count} files.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Scan stopped by user.";
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Win32 Error: 5") || ex.Message.Contains("Administrator"))
                {
                    var res = MessageBox.Show(
                        "Windows requires Administrator permissions to access raw storage sectors on this drive.\n\nWould you like to restart DataRecovery Pro as Administrator now?",
                        "Administrator Privileges Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (res == MessageBoxResult.Yes)
                    {
                        RestartAsAdministrator();
                        return;
                    }
                }
                else
                {
                    MessageBox.Show($"Scan error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }

        private void UpdateCategoryCounts(CarvedFileResult f)
        {
            CountAll++;
            if (f.Status.Contains("Deleted", StringComparison.OrdinalIgnoreCase)) CountDeleted++;
            if (f.Status.Contains("Recycle", StringComparison.OrdinalIgnoreCase)) CountRecycleBin++;

            switch (f.Category)
            {
                case FileCategory.Documents: CountDocuments++; break;
                case FileCategory.Images: CountImages++; break;
                case FileCategory.Video: CountVideo++; break;
                case FileCategory.Audio: CountAudio++; break;
                case FileCategory.Archives: CountArchives++; break;
            }

            if (!string.IsNullOrWhiteSpace(f.Extension))
            {
                string ext = f.Extension.Trim().ToLowerInvariant().TrimStart('.');
                if (!string.IsNullOrEmpty(ext) && !AvailableExtensions.Contains(ext))
                {
                    AvailableExtensions.Add(ext);
                }
            }
        }

        [RelayCommand]
        public void StopScan()
        {
            _scanCts?.Cancel();
            StatusMessage = "Stopping scan...";
        }

        [RelayCommand]
        public void SelectAllFiles()
        {
            foreach (CarvedFileResult file in FilteredFiles)
            {
                file.IsSelected = true;
            }
            _filesView?.Refresh();
        }

        [RelayCommand]
        public void DeselectAllFiles()
        {
            foreach (CarvedFileResult file in FilteredFiles)
            {
                file.IsSelected = false;
            }
            _filesView?.Refresh();
        }

        [RelayCommand]
        public async Task ExportSelectedFileAsync()
        {
            if (SelectedFile == null)
            {
                MessageBox.Show("Please select a file to export.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_activeReader == null || SelectedDevice == null) return;

            if (!SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, DestinationPath, out string secErr))
            {
                MessageBox.Show(secErr + "\n\nPlease select a destination folder on another drive or USB disk.", "Anti-Overwrite Protection", MessageBoxButton.OK, MessageBoxImage.Hand);
                BrowseDestination();
                if (!SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, DestinationPath, out _))
                {
                    return;
                }
            }

            _exporter.ResetConflictMemory();

            try
            {
                string? path = await _exporter.ExportFileAsync(
                    _activeReader,
                    SelectedFile,
                    DestinationPath,
                    CancellationToken.None,
                    ResolveConflictAsync);

                if (path != null)
                {
                    await _exporter.WriteManifestAsync(DestinationPath, DiscoveredFiles.Where(f => f.IsExported));
                    MessageBox.Show($"File successfully recovered to:\n{path}\n\nSHA-256:\n{SelectedFile.Sha256Hash}", "Recovery Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = "Export skipped by user.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task ExportAllFilesAsync()
        {
            var filesToExport = DiscoveredFiles.Where(f => f.IsSelected).ToList();
            if (filesToExport.Count == 0)
            {
                filesToExport = DiscoveredFiles.ToList();
            }

            if (filesToExport.Count == 0)
            {
                MessageBox.Show("No files discovered to export.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_activeReader == null || SelectedDevice == null) return;

            if (!SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, DestinationPath, out string secErr))
            {
                MessageBox.Show(secErr + "\n\nPlease select a destination folder on another drive or USB disk.", "Anti-Overwrite Protection", MessageBoxButton.OK, MessageBoxImage.Hand);
                BrowseDestination();
                if (!SecurityValidator.ValidateDestinationIsSafe(SelectedDevice, DestinationPath, out _))
                {
                    return;
                }
            }

            var res = MessageBox.Show($"Export {filesToExport.Count} recovered files to:\n{DestinationPath}?", "Confirm Recovery", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            _exporter.ResetConflictMemory();

            int count = 0;
            StatusMessage = "Exporting recovered files...";

            foreach (var file in filesToExport)
            {
                try
                {
                    string? path = await _exporter.ExportFileAsync(
                        _activeReader,
                        file,
                        DestinationPath,
                        CancellationToken.None,
                        ResolveConflictAsync);

                    if (path != null) count++;
                }
                catch { }
            }

            await _exporter.WriteManifestAsync(DestinationPath, DiscoveredFiles.Where(f => f.IsExported));
            StatusMessage = $"Export complete. Successfully recovered {count} files.";
            MessageBox.Show($"Successfully exported {count} files to:\n{DestinationPath}\n\nForensic manifest saved as recovery_manifest.json.", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task<(ConflictChoice Choice, bool ApplyToAll)> ResolveConflictAsync(ConflictResolutionRequest req)
        {
            var tcs = new TaskCompletionSource<(ConflictChoice, bool)>();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new ConflictDialog(req)
                {
                    Owner = Application.Current.MainWindow
                };

                if (dlg.ShowDialog() == true)
                {
                    tcs.SetResult((dlg.SelectedChoice, dlg.ApplyToAll));
                }
                else
                {
                    tcs.SetResult((ConflictChoice.Skip, false));
                }
            });

            return await tcs.Task;
        }

        partial void OnSelectedFileChanged(CarvedFileResult? value)
        {
            LoadFilePreview(value);
        }

        private void LoadFilePreview(CarvedFileResult? file)
        {
            PreviewImage = null;
            PreviewImageDimensions = string.Empty;
            PreviewText = string.Empty;
            PreviewHex = string.Empty;
            PreviewErrorMessage = string.Empty;

            if (file == null) return;

            // Always default to Details tab where Image Preview & Metadata are combined
            SelectedPreviewTabIndex = 0;

            try
            {
                byte[] previewBuffer;
                int read = 0;

                if (file.IsResident && file.ResidentData != null)
                {
                    previewBuffer = file.ResidentData;
                    read = previewBuffer.Length;
                }
                else if (_activeReader != null && file.SectorOffset >= 0)
                {
                    int maxToRead = (file.Category == FileCategory.Images) ? 8 * 1024 * 1024 : 64 * 1024;
                    int previewSize = (int)Math.Min(maxToRead, file.FileSizeBytes > 0 ? file.FileSizeBytes : maxToRead);
                    previewBuffer = new byte[previewSize];

                    if (file.DataRuns != null && file.DataRuns.Count > 1)
                    {
                        int totalRead = 0;
                        foreach (var dr in file.DataRuns)
                        {
                            if (totalRead >= previewSize) break;
                            long runOffset = dr.StartingLcn * file.BytesPerCluster;
                            int runBytes = (int)Math.Min(previewSize - totalRead, dr.ClusterCount * file.BytesPerCluster);
                            _activeReader.Seek(runOffset);
                            int runRead = _activeReader.Read(previewBuffer, totalRead, runBytes);
                            if (runRead <= 0) break;
                            totalRead += runRead;
                        }
                        read = totalRead;
                    }
                    else
                    {
                        _activeReader.Seek(file.SectorOffset);
                        read = _activeReader.Read(previewBuffer, 0, previewSize);
                    }
                }
                else
                {
                    return;
                }

                if (read <= 0) return;

                // Format Hex Dump
                int hexCount = Math.Min(512, read);
                var hexBuilder = new StringBuilder();
                for (int i = 0; i < hexCount; i += 16)
                {
                    hexBuilder.Append($"{i:X4}  ");
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < hexCount)
                            hexBuilder.Append($"{previewBuffer[i + j]:X2} ");
                        else
                            hexBuilder.Append("   ");
                    }
                    hexBuilder.Append(" |");
                    for (int j = 0; j < 16 && (i + j) < hexCount; j++)
                    {
                        byte b = previewBuffer[i + j];
                        hexBuilder.Append(b >= 32 && b <= 126 ? (char)b : '.');
                    }
                    hexBuilder.AppendLine("|");
                }
                PreviewHex = hexBuilder.ToString();

                // Format Text preview if Document / Text
                if (file.Category == FileCategory.Documents || file.Extension == "txt" || file.Extension == "log" || file.Extension == "csv" || file.Extension == "json")
                {
                    try
                    {
                        int textLen = Math.Min(4096, read);
                        PreviewText = Encoding.UTF8.GetString(previewBuffer, 0, textLen);
                    }
                    catch { }
                }

                // If image, try loading bitmap
                if (file.Category == FileCategory.Images && read > 32)
                {
                    try
                    {
                        using var ms = new MemoryStream(previewBuffer, 0, read);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        PreviewImage = bitmap;
                        PreviewImageDimensions = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
                        file.IsPreviewValid = true;
                    }
                    catch
                    {
                        PreviewImage = null;
                        PreviewImageDimensions = string.Empty;
                        file.IsPreviewValid = false;
                        PreviewErrorMessage = $"⚠️ Image format corrupted or incomplete ({file.Extension.ToUpperInvariant()} | {file.DisplaySize})\nDisk clusters may have been overwritten.";
                    }
                }
            }
            catch (Exception ex)
            {
                PreviewHex = $"Could not load preview: {ex.Message}";
            }
        }

        public static bool IsRunningAsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void RestartAsAdministrator()
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DataRecovery.App.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch { }
        }
    }
}
