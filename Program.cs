using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;


class Program
{
    static readonly HttpClient client = new HttpClient();

    static async Task Main()
    {
        await AddFile("./CV.PDF","HR","123");
        await Query("XXXX'in Telefon Numarası Ne?");
        //await DeleteFile("123");
    }

    static async Task<string> Query(string question)
    {
        var jsonData = $"{{\"query\": \"{question}\"}}";
        var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PostAsync("http://127.0.0.1:8001/query/", content);
        string result = await response.Content.ReadAsStringAsync();

        return result;
    }

    static async Task AddFile(string filePath, string category, string fileId)
    {
        var data = new
        {
            file_path = filePath.Replace("\\", "/"), // JSON uyumlu hale getir
            category = category,
            unique_id = fileId
        };

        var jsonData = JsonSerializer.Serialize(data);
        var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

        HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync("http://127.0.0.1:8000/add_file/", content);
        string result = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"Dosya Ekleme Sonucu: {result}");
    }
    static async Task DeleteFile(string fileId)
    {
        HttpResponseMessage response = await client.DeleteAsync($"http://127.0.0.1:8000/delete_file/{fileId}");
        string result = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"Dosya Silme Sonucu: {result}");
    }
}