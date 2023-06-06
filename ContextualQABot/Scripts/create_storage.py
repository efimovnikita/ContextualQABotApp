import os
import shutil

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma
from langchain.document_loaders import UnstructuredHTMLLoader
from langchain.text_splitter import CharacterTextSplitter

directory = "/home/maskedball/Downloads/langchain/sources/"
documents = []
for filename in os.listdir(directory):
    file_path = os.path.join(directory, filename)

    # Ensure we're only dealing with files (not directories)
    if os.path.isfile(file_path):
        loader = UnstructuredHTMLLoader(file_path=file_path)
        html_docs = loader.load()

        for doc in html_docs:
            documents.append(doc)

# Embed and store the texts
# Supplying a persist_directory will store the embeddings on disk
persist_directory = 'db'

# Check if the directory exists
if os.path.exists(persist_directory):
    # Use shutil.rmtree() to remove the directory and its contents
    shutil.rmtree(persist_directory)
    print("Directory removed successfully.")
else:
    print("Directory does not exist.")

text_splitter = CharacterTextSplitter(chunk_size=500, chunk_overlap=20)
texts = text_splitter.split_documents(documents=documents)

embedding = OpenAIEmbeddings()
vectordb = Chroma.from_documents(documents=texts, embedding=embedding,
                                 persist_directory=persist_directory)
vectordb.persist()
vectordb = None
