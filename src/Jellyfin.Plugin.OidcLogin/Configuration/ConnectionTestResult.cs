using System.Collections.Generic;

namespace Jellyfin.Plugin.OidcLogin.Configuration;

/// <summary>
/// The outcome of Test connection, worded for the admin.
/// </summary>
/// <param name="Succeeded">Whether discovery was fetched and fits the Issuer URL.</param>
/// <param name="Message">What worked, or why it didn't.</param>
/// <param name="Warnings">Problems that don't stop sign-in but will surprise the admin later.</param>
public sealed record ConnectionTestResult(bool Succeeded, string Message, IReadOnlyList<string> Warnings);
