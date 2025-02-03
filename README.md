# C# Local RAG LLM (DeepSeek- Embeding - VectorDB) - .NET Core

Proje Amacı:
Python Server ve C# Client Üzerinden Local olarak yapay zeka modellerini çalıştırmak, dosyalarınızı analiz ederek bu dosyaları vectorDB'ye eklemek ve yönetmek daha sonrada bu dökümanlar üzerinden ve llm bilgileri üzerinden sorgu yapmanızı sağlamak.

# Python Server Kısmı

# Kullanılan Python Sürümü: 3.10.x

# Gerekli Yüklemeler:  
pip install fastapi uvicorn torch transformers safetensors chromadb

# Alternatif VectorDB ve Embeding Modelleri: 
  pip install faiss-cpu
  pip install annoy
  pip install weaviate-client
  pip install pinecone-client
  pip install sentence-transformers

# File Sunucusu
HTTP Üzerinden dosya yolu, kategori ve uniq id göndererek gönderdiğiniz dosyayı tarayıp vector haline getirerek vectorDB içerisinde saklamanıza olanak sağlar.

# API Sunucusu
Client üzerinden soru sormanıza ve cevap almanıza olanak sağlar.

# Mevcut Embedding Modelleri:
Kullanmak istediğinzi embedding modelinin yorum satırını kaldırıp o modeli kullanmaya başlayabilirsiniz.

# Model						        Hız			  Doğruluk	  Uygulama Alanı
all-MiniLM-L6-v2			    En hızlı	Orta		    Hızlı ve düşük kaynak gerektiren uygulamalar
multi-qa-MiniLM-L6-cos-v1	Orta		  Yüksek		  Soru-cevap ve metin eşleştirme uygulamaları
BAAI/bge-large-en-v1.5		Orta		  Yüksek		  Derinlemesine dil modelleme, büyük veri kümeleri
intfloat/e5-large-v2		  Orta		  Yüksek		  Genel amaçlı metin analizi ve sınıflandırma
BAAI/bge-m3					      Yavaş		  Çok Yüksek	Büyük veri analitiği, derin öğrenme tabanlı projeler

# Mevcut VectorDB'ler
Kullanmak istediğinzi db modelinin yorum satırını kaldırıp o modeli kullanmaya başlayabilirsiniz.

Veritabanı	  Hız	      Doğruluk	GPU Desteği	  Kullanım Alanı
ChromaDB	    En hızlı	Orta	    Hayır	        Küçük/orta ölçekli projeler, hızlı entegrasyon
FAISS	        Orta	    Yüksek	  Evet	        Büyük veri kümeleri ile hızlı arama ve benzerlik hesaplama
Annoy	        En hızlı	Orta	    Hayır	        Hafif ve bellek dostu uygulamalar, hızlı arama
Weaviate	    Orta	    Yüksek	  Evet	        Semantik arama, büyük veri kümeleri
Pinecone	    Orta	    Yüksek	  Evet	        Yönetilen, bulut tabanlı uygulamalar, büyük veri projeleri

# Çalıştırma
uvicorn PythonAPIServer:app --host 127.0.0.1 --port 8001
uvicorn PythonRAGServer:app --host 127.0.0.1 --port 8000


# C# Client Örnek Kodlar
API Soru Cevap Örneği:

static async Task<string> Query(string question)
{
    var jsonData = $"{{\"query\": \"{question}\"}}";
    var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

    HttpResponseMessage response = await client.PostAsync("http://127.0.0.1:8001/query/", content);
    string result = await response.Content.ReadAsStringAsync();

    return result;
}
