// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;

namespace IceScreenTouch.App.ViewModels;

/// <summary>The text of the license state: the trial line of the status tab and the line of the About tab.</summary>
public static class LicenseLines
{
    /// <summary>
    /// The trial line: "Trial: 6 days left (until Nov 16, 14:00)". From 24 hours on it counts whole days (rounded
    /// down); from one hour to under a day it counts hours (rounded down); under an hour it counts minutes (rounded
    /// down, never below 1), so the line never says "0 days" or "0 hours".
    /// </summary>
    public static string TrialRemaining(DateTimeOffset expiration, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        var left = expiration - now;
        var until = Until(expiration, zone, culture);
        if (left >= TimeSpan.FromDays(1))
        {
            var days = (int)left.TotalDays;
            return days == 1
                ? Loc.Format(Strings.FormatTrialDayLeft, until)
                : Loc.Format(Strings.FormatTrialDaysLeft, days, until);
        }

        if (left >= TimeSpan.FromHours(1))
        {
            var hours = (int)left.TotalHours;
            return hours == 1
                ? Loc.Format(Strings.FormatTrialHourLeft, until)
                : Loc.Format(Strings.FormatTrialHoursLeft, hours, until);
        }

        var minutes = Math.Max(1, (int)left.TotalMinutes);
        return minutes == 1
            ? Loc.Format(Strings.FormatTrialMinuteLeft, until)
            : Loc.Format(Strings.FormatTrialMinutesLeft, minutes, until);
    }

    /// <summary>
    /// The end of the trial in local time as short date and short time of the UI language: "Nov 9, 14:00" in English,
    /// "11月9日 14:00" in Japanese (the year is left out: a trial lasts days at most).
    /// </summary>
    public static string Until(DateTimeOffset expiration, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        var local = TimeZoneInfo.ConvertTime(expiration, zone);
        var format = culture.DateTimeFormat;

        // "MMMM d" becomes "MMM d"; Japanese, Chinese and Korean have no month names to shorten.
        var day = local.ToString(format.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal), culture);
        var time = local.ToString(format.ShortTimePattern, culture);
        var space = culture.TwoLetterISOLanguageName is "ja" or "zh" or "ko" ? " " : ", ";
        return day + space + time;
    }

    /// <summary>The About tab's license line.</summary>
    public static string About(LicenseState state, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Kind switch
        {
            LicenseKind.Purchased => Strings.AboutLicensePurchased,
            LicenseKind.Trial when state.Expiration is { } end => Loc.Format(Strings.FormatAboutLicenseTrial, Until(end, zone, culture)),
            LicenseKind.Expired => Strings.AboutLicenseTrialEnded,
            LicenseKind.NotPackaged => Strings.AboutLicenseNotPackaged,
            _ => Strings.AboutLicenseUnknown,
        };
    }
}
