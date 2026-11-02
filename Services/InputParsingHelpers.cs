using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace TermKit.Services
{
    /// <summary>
    /// Ports ConvertTo-InputList, Test-IsEmailLikeValue, and Get-InvalidEmailLikeValues from the
    /// WinForms script. Input boxes accept comma/semicolon/newline/space separated values, which
    /// are trimmed, stripped of surrounding quote/angle-bracket characters, and deduplicated
    /// case-insensitively while preserving first-seen order.
    /// </summary>
    public static class InputParsingHelpers
    {
        private static readonly Regex SplitPattern = new(@"[,;\r\n\s]+", RegexOptions.Compiled);
        private static readonly Regex TrimCharsPattern = new(@"^[""'<]+|[""'>]+$", RegexOptions.Compiled);

        public static string[] ConvertToInputList(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return Array.Empty<string>();

            var parsedValues = new List<string>();
            var seenValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawValue in SplitPattern.Split(input))
            {
                var cleanValue = TrimCharsPattern.Replace(rawValue.Trim(), string.Empty);
                if (string.IsNullOrWhiteSpace(cleanValue)) continue;
                if (seenValues.Add(cleanValue))
                {
                    parsedValues.Add(cleanValue);
                }
            }

            return parsedValues.ToArray();
        }

        public static bool IsEmailLikeValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.Any(char.IsWhiteSpace)) return false;

            try
            {
                var mailAddress = new MailAddress(value);
                return mailAddress.Address == value;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static string[] GetInvalidEmailLikeValues(IEnumerable<string> values)
        {
            return values.Where(v => !IsEmailLikeValue(v)).ToArray();
        }
    }
}
