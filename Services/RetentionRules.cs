using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace TermKit.Services
{
    /// <summary>Pure decision rules ported from RetentionTermToolkit.ps1 so they can be tested without tenant access.</summary>
    internal static class RetentionRules
    {
        // Entra prefixes a deleted user's UPN with its object ID (no hyphens).
        public static bool IsDeletedUserMatch(string id, string? userPrincipalName, string? onPremisesSamAccountName, string? mail, string targetUpn, string targetSam) =>
            Same(userPrincipalName, targetUpn)
            || Same(userPrincipalName, id.Replace("-", string.Empty, StringComparison.Ordinal) + targetUpn)
            || Same(onPremisesSamAccountName, targetSam)
            || Same(mail, targetUpn);

        public static string NewRandomPassword(int length = 16, int nonAlphanumericChars = 4)
        {
            const string lower = "abcdefghijklmnopqrstuvwxyz";
            const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string digits = "0123456789";
            const string symbols = "!@#$%^&*()-_=+[]{}|;:,.<>/?";
            const string all = lower + upper + digits + symbols;

            var password = new List<char> { Pick(lower), Pick(upper), Pick(digits), Pick(symbols) };
            for (var i = 1; i < nonAlphanumericChars; i++) password.Add(Pick(symbols));
            while (password.Count < length) password.Add(Pick(all));
            for (var i = password.Count - 1; i > 0; i--)
            {
                var j = RandomNumberGenerator.GetInt32(i + 1);
                (password[i], password[j]) = (password[j], password[i]);
            }
            return new string(password.ToArray());
        }

        // Existing convention for terminations without retention; the daily cleanup job acts on stamped dates before today.
        public static DateTime NoRetentionEndDate(DateTime today) => today.Date.AddDays(1);

        private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

        private static bool Same(string? left, string? right) =>
            left != null && right != null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
