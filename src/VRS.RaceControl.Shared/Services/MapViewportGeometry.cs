namespace VRS.RaceControl.Shared.Services;

public readonly record struct MapViewport(double Scale, double PanX, double PanY);

/// <summary>One coordinate system for fit, zoom and bounded panning.</summary>
public static class MapViewportGeometry
{
    public static MapViewport Fit(double imageWidth, double imageHeight, double width, double height)
    {
        var scale = Math.Min(width / imageWidth, height / imageHeight);
        return Clamp(new MapViewport(scale, 0, 0), imageWidth, imageHeight, width, height);
    }

    public static MapViewport Clamp(MapViewport view, double imageWidth, double imageHeight, double width, double height) =>
        view with { PanX = ClampAxis(view.PanX, imageWidth * view.Scale, width),
                    PanY = ClampAxis(view.PanY, imageHeight * view.Scale, height) };

    private static double ClampAxis(double pan, double content, double viewport) =>
        content <= viewport ? (viewport - content) / 2 : Math.Clamp(pan, viewport - content, 0);

    public static MapViewport Zoom(MapViewport view, double scale, double anchorX, double anchorY) =>
        new(scale, anchorX - (anchorX - view.PanX) * scale / view.Scale,
                   anchorY - (anchorY - view.PanY) * scale / view.Scale);

    public static MapViewport Resize(MapViewport view, double oldWidth, double oldHeight, double width, double height) =>
        view with { PanX = view.PanX + (width - oldWidth) / 2, PanY = view.PanY + (height - oldHeight) / 2 };
}
