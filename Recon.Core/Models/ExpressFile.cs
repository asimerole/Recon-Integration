using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Recon.Core.Models;

public class ExpressFile : BaseFile // REXPR
{
    public string TypeKz { get; set; } = string.Empty;
    public string DamagedLine { get; set; } = string.Empty;
    public string Factor { get; set; } = string.Empty;
    
    // --- 1. СТАТИЧНІ REGEX (Компільовані для швидкості) ---
    // Створюються один раз на весь час роботи програми.
    private static readonly Regex DateRegex = new Regex(@"Дата\s*:?\s*(\d{2}/\d{2}/\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimeRegex = new Regex(@"Время(?:\s+пуска)?\s*:?\s*(\d{2}:\d{2}:\d{2}\.\d{3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FactorRegex = new Regex(@"Фактор пуска:\s*(.*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TypeKzRegex = new Regex(@"Повреждение.*:\s*(.*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DamagedLineRegex = new Regex(@"Поврежденная линия[^:]*:\s*(.*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fallback Regex (для параметрів типу $DP=)
    private static readonly Regex ParamDpRegex = new Regex(@"\$DP=(.*)", RegexOptions.Compiled);
    private static readonly Regex ParamTpRegex = new Regex(@"\$TP=(.*)", RegexOptions.Compiled);
    private static readonly Regex ParamSfRegex = new Regex(@"\$SF=(.*)", RegexOptions.Compiled);
    private static readonly Regex ParamLfRegex = new Regex(@"\$LF=(.*)", RegexOptions.Compiled);

    protected override Task ProcessContentSpecificAsync()
    {
        if (BinaryData == null || BinaryData.Length == 0) return Task.CompletedTask;

        try
        {
            var encoding = Encoding.GetEncoding(866);
            var content = encoding.GetString(BinaryData);
            
            string dateStr = ExtractValue(content, DateRegex);
            string timeStr = ExtractValue(content, TimeRegex);
            Factor = ExtractValue(content, FactorRegex);
            TypeKz = ExtractValue(content, TypeKzRegex);
            DamagedLine = ExtractValue(content, DamagedLineRegex);
            
            if (string.IsNullOrEmpty(dateStr)) dateStr = ExtractParamValue(content, ParamDpRegex);
            if (string.IsNullOrEmpty(timeStr)) timeStr = ExtractParamValue(content, ParamTpRegex);
            if (string.IsNullOrEmpty(Factor)) Factor = ExtractParamValue(content, ParamSfRegex);
            
            if (string.IsNullOrEmpty(TypeKz))
            {
                string lfValue = ExtractParamValue(content, ParamLfRegex).Trim();
                if (!string.IsNullOrEmpty(lfValue))
                {
                    if (lfValue == "1" || lfValue == "2" || lfValue == "3" || lfValue == "4")
                    {
                        TypeKz = $"{lfValue} фазное КЗ";
                    }
                    else
                    {
                        TypeKz = " ";
                    }
                }
            }
            
            if (!string.IsNullOrEmpty(dateStr) && !string.IsNullOrEmpty(timeStr))
            {
                string dateTimeString = $"{dateStr} {timeStr}";
                string format = "dd/MM/yyyy HH:mm:ss.fff";

                if (DateTime.TryParseExact(dateTimeString, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
                {
                    Timestamp = parsedDate;
                }
            }
        }
        catch (Exception ex)
        {
            //
        }
        
        return Task.CompletedTask;
    }

    private string ExtractValue(string content, Regex regex)
    {
        var match = regex.Match(content);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }
    
    private string ExtractParamValue(string content, Regex regex)
    {
        var match = regex.Match(content);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim(); 
        }
        return string.Empty;
    }
}