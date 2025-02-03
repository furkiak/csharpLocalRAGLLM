# C# Local RAG LLM (DeepSeek- Embeding - VectorDB) - .NET Core

## Project Objective (Proje Amacı):
To run AI models locally via a Python server and a C# client, analyze documents, add and manage them in a vector database (VectorDB), and enable querying based on both the stored documents and the LLM (Large Language Model) knowledge.

(Python Server ve C# Client Üzerinden Local olarak yapay zeka modellerini çalıştırmak, dosyalarınızı analiz ederek bu dosyaları vectorDB'ye eklemek ve yönetmek daha sonrada bu dökümanlar üzerinden ve llm bilgileri üzerinden sorgu yapmanızı sağlamak.)

## Python Server Section (Python Server Kısmı)


### Used Python Version (Kullanılan Python Sürümü): 3.10.x
### Example Model (Örnek Model): DeepSeek-r1:1.5b

### Required Installations (Gerekli Yüklemeler):  
```
pip install fastapi uvicorn torch transformers safetensors chromadb
```
### Alternative VectorDB and Embedding Models (Alternatif VectorDB ve Embeding Modelleri): 
```
  pip install faiss-cpu
  pip install annoy
  pip install weaviate-client
  pip install pinecone-client
  pip install sentence-transformers
```
## File Server (Dosya Sunucusu)
Allows you to send a file path, category, and unique ID via HTTP, process the file into a vector, and store it in the vector database.

(HTTP Üzerinden dosya yolu, kategori ve uniq id göndererek gönderdiğiniz dosyayı tarayıp vector haline getirerek vectorDB içerisinde saklamanıza olanak sağlar.)

## API Server (API Sunucusu):
Enables querying and receiving responses from the client.

(Client üzerinden soru sormanıza ve cevap almanıza olanak sağlar.)

## Available Embedding Models (Mevcut Embedding Modelleri):
You can uncomment the desired embedding model to start using it.

(Kullanmak istediğinzi embedding modelinin yorum satırını kaldırıp o modeli kullanmaya başlayabilirsiniz.)
```						     
all-MiniLM-L6-v2			    
multi-qa-MiniLM-L6-cos-v1 
BAAI/bge-large-en-v1.5		 
intfloat/e5-large-v2		  
BAAI/bge-m3					       
```
## Available VectorDBs (Mevcut VectorDB'ler):
You can uncomment the desired database model to start using it.

(Kullanmak istediğinzi db modelinin yorum satırını kaldırıp o modeli kullanmaya başlayabilirsiniz.)
```	  
ChromaDB	    
FAISS	         
Annoy	        
Weaviate	     
Pinecone	    
```
## Execution (Çalıştırma)
```
uvicorn PythonAPIServer:app --host 127.0.0.1 --port 8001
```
```
uvicorn PythonRAGServer:app --host 127.0.0.1 --port 8000
```

## C# Client Example Codes (C# Client Örnek Kodlar)
### API Query Example (API Soru Cevap Örneği):
```
static async Task<string> Query(string question)
{
    var jsonData = $"{{\"query\": \"{question}\"}}";
    var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

    HttpResponseMessage response = await client.PostAsync("http://127.0.0.1:8001/query/", content);
    string result = await response.Content.ReadAsStringAsync();

    return result;
}
```

### C# File Upload Example (C# Dosya Ekleme Örnek Kod):
```
static async Task AddFile(string filePath, string category, string fileId)
{
    var data = new
    {
        file_path = filePath.Replace("\\", "/"), 
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
```
### C# File Delete Example (C# Dosya Silme Örnek Kod):
```
   static async Task DeleteFile(string fileId)
{
    HttpResponseMessage response = await client.DeleteAsync($"http://127.0.0.1:8000/delete_file/{fileId}");
    string result = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"Dosya Silme Sonucu: {result}");
}
```
