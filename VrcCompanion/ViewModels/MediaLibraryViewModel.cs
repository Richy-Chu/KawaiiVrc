using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The Media tab: real pictures from the VRChat pictures folder, grouped by
/// world folder and month, with open-file and open-folder actions.
/// </summary>
public sealed class MediaLibraryViewModel : ViewModelBase
{
    private bool _loaded;
    private bool _isBusy;

    public MediaLibraryViewModel(string? root = null)
    {
        RootPath = string.IsNullOrWhiteSpace(root) ? MediaLibraryScanner.DefaultRoot : root;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true), () => !IsBusy);
        OpenFileCommand = new RelayCommand<string>(OpenFile, path => !string.IsNullOrWhiteSpace(path));
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public ObservableCollection<MediaGroup> Groups { get; } = new();

    public string RootPath { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand<string> OpenFileCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int PictureCount => Groups.Sum(group => group.Items.Count);

    public bool HasPictures => PictureCount > 0;

    public string SummaryText => PictureCount == 0
        ? "No VRChat pictures yet"
        : $"{PictureCount} picture{(PictureCount == 1 ? string.Empty : "s")} in {Groups.Count} group{(Groups.Count == 1 ? string.Empty : "s")}";

    public string EmptyMessage => Directory.Exists(RootPath)
        ? "No pictures in the VRChat folder yet. Take a photo in-game and press Refresh."
        : $"No VRChat pictures folder at {RootPath}.";

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync(force: false);

    public async Task LoadAsync(bool force)
    {
        if (IsBusy || (_loaded && !force))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var groups = await Task.Run(() => MediaLibraryScanner.Scan(RootPath));
            Groups.Clear();
            foreach (var group in groups)
            {
                Groups.Add(group);
            }

            _loaded = true;
            OnPropertyChanged(nameof(PictureCount));
            OnPropertyChanged(nameof(HasPictures));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(EmptyMessage));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenFolder()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(RootPath) { UseShellExecute = true });
    }
}
