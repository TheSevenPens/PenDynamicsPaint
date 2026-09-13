namespace PenDynamicsPaint.Drawing;

/// <summary>How each new stroke picks its colour.</summary>
/// <remarks>
/// This and <see cref="PressureControl"/> used to live in <c>Curves/Enums.cs</c>, next to
/// curve types they have nothing to do with. They are brush concepts: what a mark looks like,
/// not how pressure is shaped on the way to it.
/// </remarks>
public enum ColorMode
{
    Black,
    Red,

    /// <summary>A colour chosen per stroke. The chosen colour is not recoverable from settings.</summary>
    Random,
}

/// <summary>Which of a sample's two pressures a mark is being drawn from.</summary>
/// <remarks>
/// <para>
/// One gesture draws two surfaces: the processed one takes the pipeline's output, the raw one
/// takes the untouched device reading, and comparing them is the point of the application. Both
/// values live on the same <see cref="StrokeSample"/>, so anything handed a sample has to be told
/// which of them applies.
/// </para>
/// <para>
/// This exists because <see cref="IBrushEngine"/> now receives samples rather than a width that
/// was computed for it. The choice could have been made by passing the selected pressure
/// alongside the sample, which would have put the reduction back one field later and left the
/// engine unable to see anything else the sample carries.
/// </para>
/// </remarks>
public enum PressureChannel
{
    /// <summary>The pipeline's output. Drives the processed surface.</summary>
    Processed,

    /// <summary>The device reading, before any curve or smoothing. Drives the raw surface.</summary>
    Raw,
}

/// <summary>Which property of the mark the pressure signal drives.</summary>
public enum PressureControl
{
    /// <summary>Pressure sets stroke width; opacity stays at 1.</summary>
    Size,

    /// <summary>Pressure sets opacity; stroke width stays at the brush size.</summary>
    Opacity,
}
