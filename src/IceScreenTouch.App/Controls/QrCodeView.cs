// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace IceScreenTouch.App.Controls;

/// <summary>A QR code of <see cref="Text"/> (an address for the iPhone's camera), black on white with its quiet zone.</summary>
public sealed class QrCodeView : Control
{
    /// <summary>The encoded text.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    private bool[,]? _modules;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(TextProperty);
        AffectsMeasure<QrCodeView>(TextProperty);
    }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// The modules of a QR code of <paramref name="text"/> (true = dark), including the 4-module quiet zone.
    /// Error correction M.
    /// </summary>
    public static bool[,] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var modules = new bool[rows.Count, rows.Count];
        for (var y = 0; y < rows.Count; y++)
        {
            for (var x = 0; x < rows.Count; x++)
            {
                modules[y, x] = rows[y][x];
            }
        }

        return modules;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Modules() is not { } modules)
        {
            return;
        }

        var n = modules.GetLength(0);
        var size = Math.Min(Bounds.Width, Bounds.Height);

        // Whole device pixels per module keep the edges sharp.
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var module = Math.Max(1, Math.Floor(size * scale / n)) / scale;
        var side = module * n;
        var left = Math.Round((Bounds.Width - side) / 2 * scale) / scale;
        var top = Math.Round((Bounds.Height - side) / 2 * scale) / scale;
        context.FillRectangle(Brushes.White, new Rect(left, top, side, side));
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (modules[y, x])
                {
                    context.FillRectangle(Brushes.Black, new Rect(left + (x * module), top + (y * module), module, module));
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Modules() is null)
        {
            return default;
        }

        var side = Math.Min(160, Math.Min(availableSize.Width, availableSize.Height));
        return new Size(side, side);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _modules = null;
        }
    }

    private bool[,]? Modules()
    {
        if (_modules is null && !string.IsNullOrEmpty(Text))
        {
            _modules = Encode(Text);
        }

        return _modules;
    }
}
