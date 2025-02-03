from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import torch
from safetensors.torch import load_file
from transformers import AutoModelForCausalLM, AutoTokenizer
import chromadb   

 
# import faiss
# from annoy import AnnoyIndex
# import weaviate
# import pinecone

app = FastAPI()

  
DB_TYPE = "chroma"  #  "faiss", "annoy", "weaviate", "pinecone"

if DB_TYPE == "chroma":
    chroma_client = chromadb.PersistentClient(path="./chroma_db")
    collection = chroma_client.get_or_create_collection(name="documents")

# FAISS  
# elif DB_TYPE == "faiss":
#     index = faiss.read_index("faiss_index.idx")

# Annoy  
# elif DB_TYPE == "annoy":
#     annoy_index = AnnoyIndex(768, 'angular')  # 768, embedding boyutu
#     annoy_index.load("annoy_index.ann")

# Weaviate 
# elif DB_TYPE == "weaviate":
#     client = weaviate.Client("http://localhost:8080")

# Pinecone  
# elif DB_TYPE == "pinecone":
#     pinecone.init(api_key="SENİN_API_KEYİN", environment="SENİN_ENVİN")
#     pinecone_index = pinecone.Index("index_adi")

# --- DeepSeek-R1:1.5B ---
MODEL_PATH = "./LLMModel"

 
tokenizer = AutoTokenizer.from_pretrained(MODEL_PATH, local_files_only=True)
model = AutoModelForCausalLM.from_pretrained(MODEL_PATH, local_files_only=True)


MODEL_WEIGHTS_PATH = f"{MODEL_PATH}/model.safetensors"
state_dict = load_file(MODEL_WEIGHTS_PATH, device="cuda" if torch.cuda.is_available() else "cpu")


print("Loaded state_dict keys:", state_dict.keys())


model.load_state_dict(state_dict, strict=False)


device = "cuda" if torch.cuda.is_available() else "cpu"
model.to(device)

class QueryRequest(BaseModel):
    query: str

@app.post("/query/")
def query_document(request: QueryRequest):
    context = ""

 
    if DB_TYPE == "chroma":
        results = collection.query(query_texts=[request.query], n_results=3)
        if results["ids"]:
            context = " ".join(results["documents"][0])

    # FAISS  
    # elif DB_TYPE == "faiss":
    #     query_embedding = embedding_model.encode(request.query).tolist()
    #     _, I = index.search([query_embedding], 3)
    #     context = " ".join([str(doc) for doc in I[0]])

    # Annoy 
    # elif DB_TYPE == "annoy":
    #     query_embedding = embedding_model.encode(request.query).tolist()
    #     I = annoy_index.get_nns_by_vector(query_embedding, 3)
    #     context = " ".join([str(doc) for doc in I])

    # Weaviate 
    # elif DB_TYPE == "weaviate":
    #     query = {"query": request.query, "n_results": 3}
    #     results = client.query.get("Document", ["text"]).with_near_text(query).do()
    #     context = " ".join([doc["text"] for doc in results])

    # Pinecone  
    # elif DB_TYPE == "pinecone":
    #     query_embedding = embedding_model.encode(request.query).tolist()
    #     results = pinecone_index.query(vector=query_embedding, top_k=3, include_metadata=True)

   
    if not context:
        inputs = tokenizer(f"Soru: {request.query}\nCevap: ", return_tensors="pt").to(device)
    else:
        inputs = tokenizer(f"Soru: {request.query}\nCevap: {context}", return_tensors="pt").to(device)

    output = model.generate(**inputs, max_new_tokens=100)
    response = tokenizer.decode(output[0], skip_special_tokens=True)
    
    return {"answer": response}
