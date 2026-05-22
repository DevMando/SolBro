using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace SolBro
{
    public class FileOperationPlugin
    {
        private const string OutputDir = "generated_files";

        [KernelFunction("create_file")]
        [Description("Creates a file with the given content and returns the file path. Use this when a user asks you to create, write, edit, or generate a document/file. Supported formats: .txt, .csv, .json, .xml, .md, .html, .py, .cs, .js, .ts, .sql, .yml, .yaml")]
        public async Task<string> CreateFileAsync(
            [Description("The filename with extension, e.g. 'players.csv' or 'report.txt'")] string filename,
            [Description("The full text content to write to the file")] string content)
        {
            try
            {
                filename = Path.GetFileName(filename);
                if (string.IsNullOrWhiteSpace(filename))
                    return "Error: Invalid filename.";

                Directory.CreateDirectory(OutputDir);

                var uniqueName = $"{Guid.NewGuid():N}_{filename}";
                var filePath = Path.Combine(OutputDir, uniqueName);

                await File.WriteAllTextAsync(filePath, content);

                Console.WriteLine($"[FilePlugin] Created file: {filePath} ({content.Length} chars)");
                return $"GENERATED_FILE:{filePath}|FILENAME:{filename}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FilePlugin] Error creating file: {ex.Message}");
                return $"Error creating file: {ex.Message}";
            }
        }
    }
}
