import argparse
import os

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma
from langchain.text_splitter import CharacterTextSplitter
from langchain.document_loaders import BSHTMLLoader

# create parser
parser = argparse.ArgumentParser()

# add arguments
parser.add_argument("--folder", help="folder with htm files")
parser.add_argument("--key", help="OPENAI_API_KEY")

args = parser.parse_args()

# Initialize an empty list for storing documents
documents = []

# Load documents for every htm file inside folder from args.folder
for filename in os.listdir(args.folder):
    if filename.endswith('.htm') or filename.endswith('.html'):
        filepath = os.path.join(args.folder, filename)
        loader = BSHTMLLoader(filepath)
        docs = loader.load()
        for d in docs:
            documents.append(d)

text_splitter = CharacterTextSplitter(chunk_size=500, chunk_overlap=50, separator=" ")
texts = text_splitter.split_documents(documents=documents)

persist_directory = 'db'

embedding = OpenAIEmbeddings(openai_api_key=args.key)
vectordb = Chroma.from_documents(documents=texts, embedding=embedding,
                                 persist_directory=persist_directory)
vectordb.persist()
vectordb = None
