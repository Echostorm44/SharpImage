using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;
using System.CommandLine;
using Spectre.Console;

namespace SharpImage.Cli;

/// <summary>
/// CLI commands for reading and manipulating image metadata (EXIF, ICC, XMP, IPTC).
/// </summary>
public static class MetadataCommands
{
    /// <summary>
    /// Creates the 'exif' command: read, write, or strip EXIF data.
    /// </summary>
    public static Command CreateExifCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file" };
        var outputArg = new Argument<string>("output") { Description = "Output image file (for write/strip operations)" };
        outputArg.Arity = ArgumentArity.ZeroOrOne;

        var stripOption = new Option<bool>("--strip") { Description = "Remove all EXIF data" };
        var setOption = new Option<string[]>("--set") { Description = "Set EXIF tag: --set Make=Canon --set Model=EOS" };
        setOption.AllowMultipleArgumentsPerToken = true;
        var removeOption = new Option<string[]>("--remove") { Description = "Remove specific tags: --remove GPS --remove Make" };
        removeOption.AllowMultipleArgumentsPerToken = true;

        var cmd = new Command("exif", "Read, modify, or strip EXIF metadata from an image");
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(stripOption);
        cmd.Add(setOption);
        cmd.Add(removeOption);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string? output = parseResult.GetValue(outputArg);
            bool strip = parseResult.GetValue(stripOption);
            string[]? setValues = parseResult.GetValue(setOption);
            string[]? removeValues = parseResult.GetValue(removeOption);

            var image = FormatRegistry.Read(input);

            bool isModify = strip || (setValues is { Length: > 0 }) || (removeValues is { Length: > 0 });

            if (isModify)
            {
                if (output is null)
                {
                    AnsiConsole.MarkupLine("[red]Output file required for write/strip operations.[/]");
                    return;
                }

                if (strip)
                {
                    image.Metadata.ExifProfile = null;
                    AnsiConsole.MarkupLine("[green]Stripped all EXIF data.[/]");
                }
                else
                {
                    image.Metadata.ExifProfile ??= new ExifProfile();
                    // Clear raw data so tags are serialized fresh
                    image.Metadata.ExifProfile.RawData = null;

                    if (removeValues is { Length: > 0 })
                    {
                        foreach (string tagName in removeValues)
                        {
                            ushort? tagId = ResolveTagId(tagName);
                            if (tagId.HasValue)
                            {
                                image.Metadata.ExifProfile.RemoveTag(tagId.Value);
                                AnsiConsole.MarkupLine($"[yellow]Removed tag: {tagName}[/]");
                            }
                        }
                    }

                    if (setValues is { Length: > 0 })
                    {
                        foreach (string pair in setValues)
                        {
                            int eqIndex = pair.IndexOf('=');
                            if (eqIndex <= 0) continue;
                            string tagName = pair[..eqIndex].Trim();
                            string tagValue = pair[(eqIndex + 1)..].Trim();
                            ushort? tagId = ResolveTagId(tagName);
                            if (tagId.HasValue)
                            {
                                byte[] valueBytes = System.Text.Encoding.ASCII.GetBytes(tagValue + "\0");
                                image.Metadata.ExifProfile.SetTag(new ExifEntry
                                {
                                    Tag = tagId.Value,
                                    DataType = ExifDataType.Ascii,
                                    Count = (uint)valueBytes.Length,
                                    Value = valueBytes
                                });
                                AnsiConsole.MarkupLine($"[green]Set {tagName} = {tagValue}[/]");
                            }
                        }
                    }
                }

                FormatRegistry.Write(image, output);
                AnsiConsole.MarkupLine($"[green]Saved to {output}[/]");
            }
            else
            {
                // Read mode: display EXIF data
                if (image.Metadata.ExifProfile is null)
                {
                    AnsiConsole.MarkupLine("[yellow]No EXIF data found.[/]");
                    return;
                }

                var table = new Table();
                table.AddColumn("IFD");
                table.AddColumn("Tag");
                table.AddColumn("Name");
                table.AddColumn("Type");
                table.AddColumn("Value");

                bool le = image.Metadata.ExifProfile.IsLittleEndian;
                foreach ((string ifd, ExifEntry entry) in image.Metadata.ExifProfile.GetAllTags())
                {
                    // Skip pointer tags
                    if (entry.Tag == ExifTag.ExifIfdPointer || entry.Tag == ExifTag.GpsIfdPointer)
                        continue;

                    table.AddRow(
                        ifd,
                        $"0x{entry.Tag:X4}",
                        ExifTag.GetName(entry.Tag),
                        entry.DataType.ToString(),
                        entry.FormatValue(le)
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"[dim]Total: {image.Metadata.ExifProfile.Ifd0Tags.Count} IFD0 + {image.Metadata.ExifProfile.ExifTags.Count} EXIF + {image.Metadata.ExifProfile.GpsTags.Count} GPS tags[/]");
            }

            image.Dispose();
        });

        return cmd;
    }

    /// <summary>
    /// Creates the 'iccprofile' command: read, apply, or strip ICC profiles.
    /// </summary>
    public static Command CreateIccProfileCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file" };
        var outputArg = new Argument<string>("output") { Description = "Output image file (for apply/strip operations)" };
        outputArg.Arity = ArgumentArity.ZeroOrOne;

        var stripOption = new Option<bool>("--strip") { Description = "Remove the ICC profile" };
        var extractOption = new Option<string?>("--extract") { Description = "Extract ICC profile to a .icc file" };
        var applyOption = new Option<string?>("--apply") { Description = "Apply an ICC profile from a .icc file" };

        var cmd = new Command("iccprofile", "Read, extract, apply, or strip ICC color profiles");
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(stripOption);
        cmd.Add(extractOption);
        cmd.Add(applyOption);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string? output = parseResult.GetValue(outputArg);
            bool strip = parseResult.GetValue(stripOption);
            string? extractPath = parseResult.GetValue(extractOption);
            string? applyPath = parseResult.GetValue(applyOption);

            var image = FormatRegistry.Read(input);

            if (extractPath is not null)
            {
                if (image.Metadata.IccProfile is null)
                {
                    AnsiConsole.MarkupLine("[yellow]No ICC profile found.[/]");
                }
                else
                {
                    File.WriteAllBytes(extractPath, image.Metadata.IccProfile.Data);
                    AnsiConsole.MarkupLine($"[green]ICC profile extracted to {extractPath} ({image.Metadata.IccProfile.Data.Length:N0} bytes)[/]");
                }
            }
            else if (applyPath is not null)
            {
                if (output is null)
                {
                    AnsiConsole.MarkupLine("[red]Output file required for apply operation.[/]");
                    image.Dispose();
                    return;
                }

                byte[] iccData = File.ReadAllBytes(applyPath);
                image.Metadata.IccProfile = new IccProfile(iccData);
                FormatRegistry.Write(image, output);
                AnsiConsole.MarkupLine($"[green]Applied ICC profile from {applyPath} and saved to {output}[/]");
            }
            else if (strip)
            {
                if (output is null)
                {
                    AnsiConsole.MarkupLine("[red]Output file required for strip operation.[/]");
                    image.Dispose();
                    return;
                }

                image.Metadata.IccProfile = null;
                FormatRegistry.Write(image, output);
                AnsiConsole.MarkupLine($"[green]Stripped ICC profile and saved to {output}[/]");
            }
            else
            {
                // Read mode: display ICC profile info
                if (image.Metadata.IccProfile is null)
                {
                    AnsiConsole.MarkupLine("[yellow]No ICC profile found.[/]");
                }
                else
                {
                    var icc = image.Metadata.IccProfile;
                    AnsiConsole.MarkupLine($"[bold]ICC Profile[/]");
                    AnsiConsole.MarkupLine($"  Size: {icc.Data.Length:N0} bytes");
                    AnsiConsole.MarkupLine($"  Color Space: {icc.ColorSpace}");
                    if (icc.Description is not null)
                        AnsiConsole.MarkupLine($"  Description: {icc.Description}");
                }
            }

            image.Dispose();
        });

        return cmd;
    }

    /// <summary>
    /// Creates the 'metadata' command: show all metadata or strip everything.
    /// </summary>
    public static Command CreateMetadataCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file" };
        var outputArg = new Argument<string>("output") { Description = "Output image file (for strip operation)" };
        outputArg.Arity = ArgumentArity.ZeroOrOne;

        var stripOption = new Option<bool>("--strip-all") { Description = "Remove ALL metadata (EXIF, ICC, XMP, IPTC)" };

        var cmd = new Command("metadata", "Display or strip all image metadata (EXIF, ICC, XMP, IPTC)");
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(stripOption);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string? output = parseResult.GetValue(outputArg);
            bool stripAll = parseResult.GetValue(stripOption);

            var image = FormatRegistry.Read(input);

            if (stripAll)
            {
                if (output is null)
                {
                    AnsiConsole.MarkupLine("[red]Output file required for strip operation.[/]");
                    image.Dispose();
                    return;
                }

                image.Metadata.ExifProfile = null;
                image.Metadata.IccProfile = null;
                image.Metadata.Xmp = null;
                image.Metadata.IptcProfile = null;
                FormatRegistry.Write(image, output);
                AnsiConsole.MarkupLine($"[green]Stripped all metadata and saved to {output}[/]");
            }
            else
            {
                // Display all metadata
                bool anyMeta = false;

                if (image.Metadata.ExifProfile is not null)
                {
                    anyMeta = true;
                    int total = image.Metadata.ExifProfile.Ifd0Tags.Count
                              + image.Metadata.ExifProfile.ExifTags.Count
                              + image.Metadata.ExifProfile.GpsTags.Count;
                    AnsiConsole.MarkupLine($"[bold]EXIF:[/] {total} tags (IFD0: {image.Metadata.ExifProfile.Ifd0Tags.Count}, EXIF: {image.Metadata.ExifProfile.ExifTags.Count}, GPS: {image.Metadata.ExifProfile.GpsTags.Count})");

                    ShowExifValue(image.Metadata.ExifProfile, ExifTag.Make, "Make");
                    ShowExifValue(image.Metadata.ExifProfile, ExifTag.Model, "Model");
                    ShowExifValue(image.Metadata.ExifProfile, ExifTag.Software, "Software");
                    ShowExifValue(image.Metadata.ExifProfile, ExifTag.DateTimeOriginal, "DateTimeOriginal");
                }

                if (image.Metadata.IccProfile is not null)
                {
                    anyMeta = true;
                    AnsiConsole.MarkupLine($"[bold]ICC Profile:[/] {image.Metadata.IccProfile.Data.Length:N0} bytes, {image.Metadata.IccProfile.ColorSpace}");
                    if (image.Metadata.IccProfile.Description is not null)
                        AnsiConsole.MarkupLine($"  Description: {image.Metadata.IccProfile.Description}");
                }

                if (image.Metadata.Xmp is not null)
                {
                    anyMeta = true;
                    AnsiConsole.MarkupLine($"[bold]XMP:[/] {image.Metadata.Xmp.Length:N0} characters");
                }

                if (image.Metadata.IptcProfile is not null)
                {
                    anyMeta = true;
                    AnsiConsole.MarkupLine($"[bold]IPTC:[/] {image.Metadata.IptcProfile.Records.Count} records");
                    foreach (var rec in image.Metadata.IptcProfile.Records)
                    {
                        AnsiConsole.MarkupLine($"  {IptcDataSet.GetName(rec.DataSet)}: {rec.Value}");
                    }
                }

                if (!anyMeta)
                {
                    AnsiConsole.MarkupLine("[yellow]No metadata found.[/]");
                }
            }

            image.Dispose();
        });

        return cmd;
    }

    private static void ShowExifValue(ExifProfile profile, ushort tag, string label)
    {
        var entry = profile.GetTag(tag);
        if (entry.HasValue)
        {
            AnsiConsole.MarkupLine($"  {label}: {entry.Value.FormatValue(profile.IsLittleEndian)}");
        }
    }

    private static ushort? ResolveTagId(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "make" => ExifTag.Make,
            "model" => ExifTag.Model,
            "software" => ExifTag.Software,
            "datetime" => ExifTag.DateTime,
            "datetimeoriginal" => ExifTag.DateTimeOriginal,
            "datetimedigitized" => ExifTag.DateTimeDigitized,
            "imagedescription" => ExifTag.ImageDescription,
            "artist" => ExifTag.Artist,
            "copyright" => ExifTag.Copyright,
            "orientation" => ExifTag.Orientation,
            "lensmodel" => ExifTag.LensModel,
            "lensmake" => ExifTag.LensMake,
            "gps" => ExifTag.GpsIfdPointer,
            _ => TryParseHexTag(name)
        };
    }

    private static ushort? TryParseHexTag(string name)
    {
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(name[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort result))
        {
            return result;
        }
        return null;
    }
}
