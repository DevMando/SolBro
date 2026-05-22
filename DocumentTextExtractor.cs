using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace SolBro
{
    public static class DocumentTextExtractor
    {
        private const int MaxTextLength = 12_000;

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".log", ".json", ".xml", ".md" };

        public static async Task<string> ExtractTextAsync(Stream stream, string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            try
            {
                var text = ext switch
                {
                    ".pdf" => ExtractFromPdf(stream),
                    ".xlsx" => ExtractFromExcel(stream),
                    ".docx" => ExtractFromWord(stream),
                    ".csv" => ExtractFromCsv(stream),
                    _ when TextExtensions.Contains(ext) => await ExtractFromText(stream),
                    _ => $"Unsupported file type: {ext}"
                };

                return Truncate(text);
            }
            catch (Exception ex)
            {
                return $"Error extracting text from {fileName}: {ex.Message}";
            }
        }

        private static string ExtractFromPdf(Stream stream)
        {
            try
            {
                using var document = PdfDocument.Open(stream);
                var sb = new StringBuilder();
                Console.WriteLine($"[PDF] Pages: {document.NumberOfPages}");

                foreach (var page in document.GetPages())
                {
                    var words = page.GetWords().ToList();
                    if (words.Count > 0)
                    {
                        sb.AppendLine(string.Join(" ", words.Select(w => w.Text)));
                    }
                    else
                    {
                        var letters = page.Letters.ToList();
                        if (letters.Count > 0)
                        {
                            sb.AppendLine(string.Join("", letters.Select(l => l.Value)));
                        }
                        else
                        {
                            var pageText = page.Text;
                            if (!string.IsNullOrWhiteSpace(pageText))
                                sb.AppendLine(pageText);
                        }
                    }
                }

                var text = sb.ToString().Trim();
                return string.IsNullOrEmpty(text) ? "PDF contains no extractable text." : text;
            }
            catch (Exception ex)
            {
                return $"Error reading PDF: {ex.Message}";
            }
        }

        private static string ExtractFromExcel(Stream stream)
        {
            try
            {
                using var workbook = new XLWorkbook(stream);
                var sb = new StringBuilder();

                foreach (var worksheet in workbook.Worksheets)
                {
                    sb.AppendLine($"--- Sheet: {worksheet.Name} ---");
                    var range = worksheet.RangeUsed();
                    if (range == null) continue;

                    foreach (var row in range.Rows())
                    {
                        var cells = row.Cells().Select(c => c.GetFormattedString());
                        sb.AppendLine(string.Join("\t", cells));
                    }
                    sb.AppendLine();
                }

                var text = sb.ToString().Trim();
                return string.IsNullOrEmpty(text) ? "Excel file contains no data." : text;
            }
            catch (Exception ex)
            {
                return $"Error reading Excel file: {ex.Message}";
            }
        }

        private static string ExtractFromWord(Stream stream)
        {
            try
            {
                using var doc = WordprocessingDocument.Open(stream, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body == null)
                    return "Word document contains no content.";

                var sb = new StringBuilder();
                foreach (var paragraph in body.Elements<Paragraph>())
                {
                    sb.AppendLine(paragraph.InnerText);
                }

                var text = sb.ToString().Trim();
                return string.IsNullOrEmpty(text) ? "Word document contains no extractable text." : text;
            }
            catch (Exception ex)
            {
                return $"Error reading Word document: {ex.Message}";
            }
        }

        private static string ExtractFromCsv(Stream stream)
        {
            try
            {
                using var reader = new StreamReader(stream);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    BadDataFound = null
                });

                var sb = new StringBuilder();
                csv.Read();
                csv.ReadHeader();
                if (csv.HeaderRecord != null)
                    sb.AppendLine(string.Join("\t", csv.HeaderRecord));

                while (csv.Read())
                {
                    var fields = new List<string>();
                    for (int i = 0; i < (csv.HeaderRecord?.Length ?? 0); i++)
                    {
                        fields.Add(csv.GetField(i) ?? "");
                    }
                    sb.AppendLine(string.Join("\t", fields));
                }

                var text = sb.ToString().Trim();
                return string.IsNullOrEmpty(text) ? "CSV file contains no data." : text;
            }
            catch (Exception ex)
            {
                return $"Error reading CSV file: {ex.Message}";
            }
        }

        private static async Task<string> ExtractFromText(Stream stream)
        {
            try
            {
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                return string.IsNullOrEmpty(text) ? "File is empty." : text;
            }
            catch (Exception ex)
            {
                return $"Error reading text file: {ex.Message}";
            }
        }

        private static string Truncate(string text)
        {
            if (text.Length <= MaxTextLength)
                return text;

            return text[..MaxTextLength] + $"\n[Truncated — showing first 12,000 of {text.Length} characters]";
        }
    }
}
