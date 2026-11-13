using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MediaThumbnailRenderTests
{
    private const int PageSize = 48;
    private const int PictureCount = 937;
    private const int LastPageCount = 25;
    private const int LastPageStart = PictureCount - LastPageCount;

    [Fact]
    public void Page20ThumbnailsRenderAndRenderAgainAfterLeavingMedia()
    {
        RunOnSta(() =>
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }

            var root = Path.Combine(
                Path.GetTempPath(),
                "vrccompanion-media-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Window? window = null;

            try
            {
                var expectedColors = CreatePage20Images(root);
                var items = Enumerable.Range(0, PictureCount)
                    .Select(index => new MediaItem(
                        index >= LastPageStart
                            ? Path.Combine(root, $"page20-{index:D3}.png")
                            : Path.Combine(root, $"other-{index:D3}.png"),
                        "VRChat",
                        new DateTime(2025, 6, 1).AddMinutes(index)))
                    .ToList();
                var group = new MediaGroup(
                    "VRChat · June 2025",
                    "VRChat",
                    new DateTime(2025, 6, 1),
                    items);
                var viewModel = new MediaLibraryViewModel(
                    "C:\\pictures",
                    (_, _) => new[] { group },
                    cachePath: Path.Combine(root, "index.json"));

                viewModel.LoadAsync(force: true).GetAwaiter().GetResult();
                Assert.Equal(20, viewModel.PageCount);

                for (var page = 1; page < viewModel.PageCount; page++)
                {
                    viewModel.NextPageCommand.Execute(null);
                }

                var pageCards = viewModel.PageCards.Where(card => card.HasItem).ToList();
                Assert.Equal(LastPageCount, pageCards.Count);
                Assert.Equal("Page 20 of 20", viewModel.PageText);

                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                var itemsControl = CreateMediaItems(pageCards);
                window = new Window
                {
                    Width = 1200,
                    Height = 900,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 100,
                    Top = 100,
                    Content = itemsControl
                };
                window.Show();

                WaitForThumbnails(itemsControl);
                var images = FindVisualChildren<AsyncImage>(itemsControl)
                    .Where(image => image.SourceUrl is not null)
                    .ToList();
                Assert.Equal(LastPageCount, images.Count);
                AssertThumbnailsRendered(images, expectedColors);
                AssertCacheLimit();
                var cachedImages = images.ToDictionary(
                    image => Assert.IsType<string>(image.SourceUrl),
                    image => image.LoadedImage!);

                // Media stays cached in MainWindow; leaving it hides the section.
                itemsControl.Visibility = Visibility.Collapsed;
                WaitUntil(
                    () => images.All(image => image.LoadedImage is null),
                    "Timed out waiting for the hidden page to release its decoded images.");
                Assert.All(images, image => Assert.Null(image.LoadedImage));

                itemsControl.Visibility = Visibility.Visible;
                WaitForThumbnails(itemsControl);
                var reenteredImages = FindVisualChildren<AsyncImage>(itemsControl)
                    .Where(image => image.SourceUrl is not null)
                    .ToList();
                Assert.Equal(LastPageCount, reenteredImages.Count);
                AssertThumbnailsRendered(reenteredImages, expectedColors);
                foreach (var image in reenteredImages)
                {
                    var path = Assert.IsType<string>(image.SourceUrl);
                    Assert.Same(cachedImages[path], image.LoadedImage);
                }

                AssertCacheLimit();
            }
            finally
            {
                window?.Close();
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });
    }

    private static Dictionary<string, Color> CreatePage20Images(string root)
    {
        var expected = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (var index = LastPageStart; index < PictureCount; index++)
        {
            var color = Page20Color(index);
            var path = Path.Combine(root, $"page20-{index:D3}.png");
            CreateSolidPng(path, color);
            expected[path] = color;
        }

        return expected;
    }

    private static Color Page20Color(int index)
        => Color.FromRgb(
            (byte)(20 + index * 8),
            (byte)(50 + index * 6),
            (byte)(210 - index * 4));

    private static void CreateSolidPng(string path, Color color)
    {
        const int size = 320;
        const int stride = size * 4;
        var pixels = new byte[stride * size];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static ItemsControl CreateMediaItems(IReadOnlyList<MediaCard> cards)
    {
        var itemsControl = new ItemsControl
        {
            Width = 1160,
            ItemsSource = cards
        };
        itemsControl.ItemsPanel = new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(WrapPanel)));

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 8));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.WidthProperty, 220d);
        border.SetValue(FrameworkElement.HeightProperty, 140d);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(21, 26, 36)));
        border.SetValue(Border.ClipToBoundsProperty, true);

        var image = new FrameworkElementFactory(typeof(AsyncImage));
        image.SetValue(AsyncImage.DecodeWidthProperty, 160);
        image.SetValue(AsyncImage.CornerRadiusProperty, 8d);
        image.SetBinding(
            AsyncImage.SourceUrlProperty,
            new Binding(nameof(MediaCard.Path)));

        border.AppendChild(image);
        stack.AppendChild(border);
        itemsControl.ItemTemplate = new DataTemplate { VisualTree = stack };
        return itemsControl;
    }

    private static void WaitForThumbnails(ItemsControl itemsControl)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var images = FindVisualChildren<AsyncImage>(itemsControl)
                .Where(image => image.SourceUrl is not null)
                .ToList();
            if (images.Count == LastPageCount &&
                images.All(image => image.LoadedImage is not null && image.ImageOpacity > 0.99))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                var details = string.Join(
                    ", ",
                    images.Select(image =>
                        $"{Path.GetFileName(image.SourceUrl)}: loaded={image.LoadedImage is not null}, visible={image.IsVisible}, loadedEvent={image.IsLoaded}, opacity={image.ImageOpacity:F2}"));
                Assert.Fail(
                    $"Timed out waiting for the page-20 thumbnails to decode. " +
                    $"Visible={itemsControl.IsVisible}, images={images.Count}; {details}");
            }

            PumpDispatcher();
        }
    }

    private static void AssertThumbnailsRendered(
        IReadOnlyList<AsyncImage> images,
        IReadOnlyDictionary<string, Color> expectedColors)
    {
        foreach (var image in images)
        {
            var path = Assert.IsType<string>(image.SourceUrl);
            Assert.True(expectedColors.TryGetValue(path, out var expected), $"Unexpected thumbnail source: {path}");
            Assert.NotNull(image.LoadedImage);

            var actual = RenderCenterPixel(image);
            Assert.True(actual.A > 240, $"{path} rendered without an opaque thumbnail.");
            Assert.True(
                Math.Abs(actual.R - expected.R) <= 12 &&
                Math.Abs(actual.G - expected.G) <= 12 &&
                Math.Abs(actual.B - expected.B) <= 12,
                $"{path} rendered {actual} instead of {expected}.");
        }
    }

    private static Color RenderCenterPixel(AsyncImage image)
    {
        var width = (int)Math.Round(image.ActualWidth);
        var height = (int)Math.Round(image.ActualHeight);
        Assert.True(width > 1 && height > 1, "The thumbnail had no layout size.");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(image);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var offset = ((height / 2) * width + width / 2) * 4;
        return Color.FromArgb(
            pixels[offset + 3],
            pixels[offset + 2],
            pixels[offset + 1],
            pixels[offset]);
    }

    private static void AssertCacheLimit()
    {
        Assert.Equal(64L * 1024 * 1024, ImageLoader.MemoryCacheLimitBytes);
        Assert.InRange(
            ImageLoader.Shared.MemoryCacheBytes,
            0,
            ImageLoader.MemoryCacheLimitBytes);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.True(condition(), message);
                return;
            }

            PumpDispatcher();
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Media thumbnail render test did not finish.");
        }

        failure?.Throw();
    }
}
