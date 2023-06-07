import argparse

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma
from langchain.chat_models import ChatOpenAI
from langchain.chains import RetrievalQA

# create parser
parser = argparse.ArgumentParser()

# add arguments
parser.add_argument("--query", help="the query for the QA system")
parser.add_argument("--key", help="OPENAI_API_KEY")

args = parser.parse_args()

embedding = OpenAIEmbeddings(openai_api_key=args.key)
persist_directory = 'db'

# Now we can load the persisted database from disk, and use it as normal.
vectordb = Chroma(persist_directory=persist_directory, embedding_function=embedding)

qa = RetrievalQA.from_llm(llm=ChatOpenAI(openai_api_key=args.key), retriever=vectordb.as_retriever())

result = qa.run(args.query)

print(result)
