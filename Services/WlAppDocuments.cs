using System.Linq;

namespace Veiculando.WhiteLabel.Api.Services;

public static class WlAppDocuments
{
    public static string Normalize(string value) => new((value ?? "").Where(ch => ch >= '0' && ch <= '9').ToArray());
    public static bool Valid(string value, string type)
    {
        var digits = Normalize(value);
        if (digits.Length != (type == "pf" ? 11 : 14) || digits.Distinct().Count() == 1) return false;
        if (type == "pf")
            return Digit(digits[..9], new[] {10,9,8,7,6,5,4,3,2}) == digits[9] - '0' &&
                   Digit(digits[..10], new[] {11,10,9,8,7,6,5,4,3,2}) == digits[10] - '0';
        return type == "pj" && Digit(digits[..12], new[] {5,4,3,2,9,8,7,6,5,4,3,2}) == digits[12] - '0' &&
               Digit(digits[..13], new[] {6,5,4,3,2,9,8,7,6,5,4,3,2}) == digits[13] - '0';
    }
    private static int Digit(string digits, int[] weights)
    { var remainder = digits.Select((ch, i) => (ch - '0') * weights[i]).Sum() % 11; return remainder < 2 ? 0 : 11 - remainder; }
}
