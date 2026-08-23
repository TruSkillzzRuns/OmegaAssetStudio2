using System;
using System.Globalization;
using System.Numerics;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class MaterialParameter : NotifyPropertyChangedBase
{
    private string name = string.Empty;
    private string category = string.Empty;
    private float? scalarValue;
    private Vector4? vectorValue;
    private float? defaultScalarValue;
    private Vector4? defaultVectorValue;

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    public string Category
    {
        get => category;
        set => SetProperty(ref category, value);
    }

    public float? ScalarValue
    {
        get => scalarValue;
        set
        {
            if (SetProperty(ref scalarValue, value))
                OnPropertyChanged(nameof(ScalarValueText));
        }
    }

    public Vector4? VectorValue
    {
        get => vectorValue;
        set
        {
            if (SetProperty(ref vectorValue, value))
            {
                OnPropertyChanged(nameof(VectorRText));
                OnPropertyChanged(nameof(VectorGText));
                OnPropertyChanged(nameof(VectorBText));
                OnPropertyChanged(nameof(VectorAText));
                OnPropertyChanged(nameof(VectorPreviewColor));
            }
        }
    }

    public string VectorRText
    {
        get => vectorValue.HasValue ? vectorValue.Value.X.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => SetVectorChannel(0, value);
    }

    public string VectorGText
    {
        get => vectorValue.HasValue ? vectorValue.Value.Y.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => SetVectorChannel(1, value);
    }

    public string VectorBText
    {
        get => vectorValue.HasValue ? vectorValue.Value.Z.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => SetVectorChannel(2, value);
    }

    public string VectorAText
    {
        get => vectorValue.HasValue ? vectorValue.Value.W.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set => SetVectorChannel(3, value);
    }

    public Windows.UI.Color VectorPreviewColor
    {
        get
        {
            Vector4 v = vectorValue ?? Vector4.Zero;
            byte r = LinearChannelToByte(v.X);
            byte g = LinearChannelToByte(v.Y);
            byte b = LinearChannelToByte(v.Z);
            byte a = LinearChannelToByte(v.W);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
        set
        {
            VectorValue = new Vector4(value.R / 255f, value.G / 255f, value.B / 255f, value.A / 255f);
        }
    }

    private void SetVectorChannel(int channel, string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
            !float.TryParse(text, out parsed))
        {
            return;
        }

        Vector4 current = vectorValue ?? Vector4.Zero;
        Vector4 next = channel switch
        {
            0 => new Vector4(parsed, current.Y, current.Z, current.W),
            1 => new Vector4(current.X, parsed, current.Z, current.W),
            2 => new Vector4(current.X, current.Y, parsed, current.W),
            3 => new Vector4(current.X, current.Y, current.Z, parsed),
            _ => current
        };

        VectorValue = next;
    }

    private static byte LinearChannelToByte(float channel)
    {
        float clamped = Math.Clamp(channel, 0f, 1f);
        return (byte)Math.Clamp((int)MathF.Round(clamped * 255f), 0, 255);
    }

    public float? DefaultScalarValue
    {
        get => defaultScalarValue;
        set => SetProperty(ref defaultScalarValue, value);
    }

    public Vector4? DefaultVectorValue
    {
        get => defaultVectorValue;
        set => SetProperty(ref defaultVectorValue, value);
    }

    public string ScalarValueText
    {
        get => scalarValue.HasValue ? scalarValue.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ScalarValue = null;
                return;
            }

            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                float.TryParse(value, out parsed))
            {
                ScalarValue = parsed;
            }
        }
    }

    public MaterialParameter Clone()
    {
        return new MaterialParameter
        {
            Name = Name,
            Category = Category,
            ScalarValue = ScalarValue,
            VectorValue = VectorValue,
            DefaultScalarValue = DefaultScalarValue,
            DefaultVectorValue = DefaultVectorValue
        };
    }

    public void CopyFrom(MaterialParameter source)
    {
        Name = source.Name;
        Category = source.Category;
        ScalarValue = source.ScalarValue;
        VectorValue = source.VectorValue;
        DefaultScalarValue = source.DefaultScalarValue;
        DefaultVectorValue = source.DefaultVectorValue;
    }
}

