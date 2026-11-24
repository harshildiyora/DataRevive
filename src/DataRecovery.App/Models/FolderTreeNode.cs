using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using DataRecovery.Core.Engine;
using DataRecovery.Core.Storage;

namespace DataRecovery.App.Models
{
    public class FolderTreeNode : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _fullPath = string.Empty;
        private string _icon = "📁";
        private bool _isExpanded = false;
        private bool _isSelected;
        private bool? _isChecked = false;
        private FolderTreeNode? _parent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public FolderTreeNode(string name, string fullPath, string icon = "📁", FolderTreeNode? parent = null)
        {
            _name = name;
            _fullPath = fullPath;
            _icon = icon;
            _parent = parent;
            _isExpanded = false;
        }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string FullPath
        {
            get => _fullPath;
            set { if (_fullPath != value) { _fullPath = value; OnPropertyChanged(); } }
        }

        public string Icon
        {
            get
            {
                if (IsRoot) return "💾";
                if (Name.StartsWith("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return "🗑️";
                if (Name.Equals("Raw Carved Files", StringComparison.OrdinalIgnoreCase)) return "📦";
                if (Name.Equals("Documents", StringComparison.OrdinalIgnoreCase)) return "📄";
                if (Name.Equals("Images", StringComparison.OrdinalIgnoreCase)) return "🖼️";
                if (Name.Equals("Video", StringComparison.OrdinalIgnoreCase)) return "🎬";
                if (Name.Equals("Audio", StringComparison.OrdinalIgnoreCase)) return "🎵";
                return _isExpanded ? "📂" : "📁";
            }
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public FolderTreeNode? Parent => _parent;

        public ObservableCollection<FolderTreeNode> Children { get; } = new();

        public List<CarvedFileResult> Files { get; } = new();

        public bool? IsChecked
        {
            get => _isChecked;
            set => SetCheckedState(value, updateChildren: true, updateParent: true);
        }

        private void SetCheckedState(bool? value, bool updateChildren, bool updateParent)
        {
            if (_isChecked == value) return;

            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            // Propagate selection to files in this folder
            if (value.HasValue)
            {
                bool select = value.Value;
                foreach (var file in Files)
                {
                    file.IsSelected = select;
                }
            }

            // Propagate selection down to child folders
            if (updateChildren && value.HasValue)
            {
                foreach (var child in Children)
                {
                    child.SetCheckedState(value, updateChildren: true, updateParent: false);
                }
            }

            // Propagate selection state up to parent folder
            if (updateParent && _parent != null)
            {
                _parent.VerifyCheckState();
            }
        }

        public void VerifyCheckState()
        {
            bool? state = null;
            for (int i = 0; i < Children.Count; i++)
            {
                bool? current = Children[i].IsChecked;
                if (i == 0)
                {
                    state = current;
                }
                else if (state != current)
                {
                    state = null; // Indeterminate
                    break;
                }
            }
            SetCheckedState(state, updateChildren: false, updateParent: true);
        }

        public int TotalFilesCount => Files.Count + Children.Sum(c => c.TotalFilesCount);

        public long TotalBytes => Files.Sum(f => f.FileSizeBytes) + Children.Sum(c => c.TotalBytes);

        public string DisplaySize => StorageDevice.FormatBytes(TotalBytes);

        public string DisplayBadge => TotalFilesCount > 0 ? $"{TotalFilesCount}" : string.Empty;

        public bool HasFiles => TotalFilesCount > 0;

        public bool IsRoot => _parent == null;

        public string DisplayType => IsRoot ? "Local Disk" : "File folder";

        public string DisplayDate => Files.Count > 0 ? (Files.Max(f => f.DisplayDate) ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm")) : DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        public string FileLocation => !string.IsNullOrEmpty(Parent?.FullPath) ? Parent.FullPath : FullPath;

        public void NotifyTotalsChanged()
        {
            OnPropertyChanged(nameof(TotalFilesCount));
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(DisplaySize));
            OnPropertyChanged(nameof(DisplayBadge));
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(IsRoot));
            _parent?.NotifyTotalsChanged();
        }

        public void NotifyAllTotalsChanged()
        {
            foreach (var child in Children)
            {
                child.NotifyAllTotalsChanged();
            }
            OnPropertyChanged(nameof(TotalFilesCount));
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(DisplaySize));
            OnPropertyChanged(nameof(DisplayBadge));
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(IsRoot));
        }

        /// <summary>
        /// Retrieves all files recursively contained within this folder and all subfolders.
        /// </summary>
        public IEnumerable<CarvedFileResult> GetAllFilesRecursive()
        {
            foreach (var f in Files)
            {
                yield return f;
            }
            foreach (var child in Children)
            {
                foreach (var f in child.GetAllFilesRecursive())
                {
                    yield return f;
                }
            }
        }

        /// <summary>
        /// Finds or creates a nested subfolder given a relative path like "public\assets\images".
        /// </summary>
        public FolderTreeNode GetOrCreateSubfolder(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "\\" || relativePath == "/")
                return this;

            string[] parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            FolderTreeNode current = this;

            foreach (var part in parts)
            {
                var existing = current.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    string childPath = Path.Combine(current.FullPath, part);
                    existing = new FolderTreeNode(part, childPath, "📁", current);
                    current.Children.Add(existing);
                }
                current = existing;
            }

            return current;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
