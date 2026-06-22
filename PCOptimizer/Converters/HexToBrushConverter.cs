using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PCOptimizer.Converters
{
    /// <summary>Converte uma string "#RRGGBB" em SolidColorBrush para uso em bindings.</summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is string hex && !string.IsNullOrWhiteSpace(hex))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch { /* cor inválida: cai no padrão */ }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
