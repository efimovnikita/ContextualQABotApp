import argparse

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma
from langchain.text_splitter import CharacterTextSplitter
from langchain.document_loaders import TextLoader

# create parser
parser = argparse.ArgumentParser()

# add arguments
parser.add_argument("--filename", help="text file that needs to be analyzed")
parser.add_argument("--key", help="OPENAI_API_KEY")

args = parser.parse_args()

loader = TextLoader(f'sources/{args.filename}', encoding='utf8')
documents = loader.load()

text_splitter = CharacterTextSplitter(chunk_size=1000, chunk_overlap=50)
texts = text_splitter.split_documents(documents=documents)

persist_directory = 'db'

embedding = OpenAIEmbeddings(openai_api_key=args.key)
vectordb = Chroma.from_documents(documents=texts, embedding=embedding,
                                 persist_directory=persist_directory)
vectordb.persist()
vectordb = None
