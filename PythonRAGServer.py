from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import os
import chromadb 

# import faiss
# from annoy import AnnoyIndex
# import weaviate
# import pinecone
from sentence_transformers import SentenceTransformer
import threading
import time
from datetime import datetime

app = FastAPI()


start_time = datetime.now()


embedding_model = SentenceTransformer("all-MiniLM-L6-v2")
# embedding_model = SentenceTransformer("multi-qa-MiniLM-L6-cos-v1")
# embedding_model = SentenceTransformer("BAAI/bge-large-en-v1.5")
# embedding_model = SentenceTransformer("intfloat/e5-large-v2")
# embedding_model = SentenceTransformer("BAAI/bge-m3")

#
DB_TYPE = "chroma"  # "chroma", "faiss", "annoy", "weaviate", "pinecone"

if DB_TYPE == "chroma":
    chroma_client = chromadb.PersistentClient(path="./chroma_db")
    collection = chroma_client.get_or_create_collection(name="documents")

# FAISS
# elif DB_TYPE == "faiss": 
#     index = faiss.IndexFlatL2(768)    
#     # index.add(np.array(embedding))  

# Annoy 
# elif DB_TYPE == "annoy":
#     # Annoy index'i 768 boyutunda ve angular mesafeye dayalı olarak tanımlanır
#     annoy_index = AnnoyIndex(768, 'angular')
#     # Burada index yükleme ve ekleme işlemleri yapılabilir
#     # annoy_index.add_item(unique_id, embedding)

# Weaviate 
# elif DB_TYPE == "weaviate": 
#     client = weaviate.Client("http://localhost:8080") 
#     # client.data_object.create({"embedding": embedding}, class_name="Document")

# Pinecone 
# elif DB_TYPE == "pinecone": 
#     pinecone.init(api_key="YOUR_API_KEY", environment="us-west1-gcp")
#     index = pinecone.Index("document_index") 
#     # index.upsert([(unique_id, embedding)])

class DocumentRequest(BaseModel):
    file_path: str
    category: str
    unique_id: str

@app.post("/add_file/")
def add_file(request: DocumentRequest):
    if not os.path.exists(request.file_path):
        raise HTTPException(status_code=404, detail="File not found")

    with open(request.file_path, "r", encoding="utf-8") as file:
        content = file.read()
    
    embedding = embedding_model.encode(content).tolist()

    if DB_TYPE == "chroma":
        collection.add(
            ids=[request.unique_id],
            embeddings=[embedding],
            metadatas=[{"category": request.category, "file_path": request.file_path}]
        )

    # FAISS 
    # elif DB_TYPE == "faiss":
    #     index.add(np.array(embedding))  # Burada FAISS'e ekleme yapılır

    # Annoy 
    # elif DB_TYPE == "annoy":
    #     annoy_index.add_item(request.unique_id, embedding)

    # Weaviate 
    # elif DB_TYPE == "weaviate":
    #     client.data_object.create({"embedding": embedding, "category": request.category, "file_path": request.file_path}, class_name="Document")

    # Pinecone
    # elif DB_TYPE == "pinecone":
    #     index.upsert([(request.unique_id, embedding)])

    return {"message": "Document added successfully"}

@app.delete("/delete_file/{unique_id}")
def delete_file(unique_id: str):
    if DB_TYPE == "chroma":
        collection.delete(ids=[unique_id])
    
    # FAISS 
    # elif DB_TYPE == "faiss":
    #     index.remove_ids([unique_id])

    # Annoy 
    # elif DB_TYPE == "annoy":
    #     annoy_index.remove_item(unique_id)

    # Weaviate 
    # elif DB_TYPE == "weaviate":
    #     client.data_object.delete(unique_id, class_name="Document")

    # Pinecone
    # elif DB_TYPE == "pinecone":
    #     index.delete(ids=[unique_id])

    return {"message": "Document deleted successfully"}


def print_uptime():
    while True:
        current_time = datetime.now()
        uptime = current_time - start_time
        days, seconds = divmod(uptime.total_seconds(), 86400)
        hours, seconds = divmod(seconds, 3600)
        minutes, seconds = divmod(seconds, 60)

        os.system('cls' if os.name == 'nt' else 'clear')  # (Windows: cls, Linux/Mac: clear)
        print(f" Çalışma süresi: {int(days)} gün, {int(hours)} saat, {int(minutes)} dakika, {int(seconds)} saniye")

        time.sleep(1)  


threading.Thread(target=print_uptime, daemon=True).start()
