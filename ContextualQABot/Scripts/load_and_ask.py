import argparse
import subprocess

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma
from langchain.chat_models import ChatOpenAI
from langchain.chains import RetrievalQA

# create parser
parser = argparse.ArgumentParser()

# add arguments
parser.add_argument("query", help="the query for the QA system")
parser.add_argument("--no-cache", help="whether to run another script", action="store_true")

args = parser.parse_args()

if args.no_cache:
    subprocess.run(["python", "create_storage.py"], check=True)

embedding = OpenAIEmbeddings()
persist_directory = 'db'

# Now we can load the persisted database from disk, and use it as normal.
vectordb = Chroma(persist_directory=persist_directory, embedding_function=embedding)

qa = RetrievalQA.from_llm(llm=ChatOpenAI(), retriever=vectordb.as_retriever())

result = qa.run(args.query)

print(result)
