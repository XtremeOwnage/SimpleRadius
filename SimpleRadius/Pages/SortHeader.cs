namespace SimpleRadius.Pages;

/// <summary>View model for the _SortHeader partial: one clickable column heading.</summary>
public sealed record SortHeader(TableSort Sort, string Column, string Label, string? CssClass = null);
