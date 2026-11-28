using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace TuckPane.Services;

// All leases for a given compositor are created and released on its UI thread.
// The last visible grain layer releases the decoded tile and the effect graph.
internal sealed class GlassGrainTile : IDisposable
{
    private static readonly ConditionalWeakTable<Compositor, GlassGrainTile> Tiles = new();
    private readonly Compositor _compositor;
    private readonly LoadedImageSurface _tile;
    private readonly CompositionSurfaceBrush _source;
    internal CompositionEffectBrush Brush { get; }
    private int _leases;

    private GlassGrainTile(Compositor compositor)
    {
        _compositor = compositor;
        using var wrap = new BorderEffect
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap,
            Source = new CompositionEffectSourceParameter("Tile")
        };
        using CompositionEffectFactory factory = compositor.CreateEffectFactory(wrap);
        Brush = factory.CreateBrush();
        try
        {
            _tile = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/GlassGrain.png"));
            _tile.LoadCompleted += LoadCompleted;
            _source = compositor.CreateSurfaceBrush(_tile);
            _source.Stretch = CompositionStretch.None;
            _source.HorizontalAlignmentRatio = 0;
            _source.VerticalAlignmentRatio = 0;
            Brush.SetSourceParameter("Tile", _source);
        }
        catch
        {
            Brush.Dispose();
            _source?.Dispose();
            if (_tile is not null)
            {
                _tile.LoadCompleted -= LoadCompleted;
                _tile.Dispose();
            }
            throw;
        }
    }

    private static void LoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
    {
        if (args.Status != LoadedImageSourceLoadStatus.Success)
            AppLogger.Error($"无法加载玻璃颗粒纹理（{args.Status}）。", null);
    }

    internal static GlassGrainTile Acquire(Compositor compositor)
    {
        var tile = Tiles.GetValue(compositor, static key => new GlassGrainTile(key));
        tile._leases++;
        return tile;
    }

    public void Dispose()
    {
        if (--_leases != 0) return;
        Tiles.Remove(_compositor);
        Brush.Dispose();
        _source.Dispose();
        _tile.LoadCompleted -= LoadCompleted;
        _tile.Dispose();
    }
}
