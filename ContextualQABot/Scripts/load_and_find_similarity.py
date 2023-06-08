import argparse

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma

# create parser
parser = argparse.ArgumentParser()

# add arguments
parser.add_argument("--query", help="the query for the QA system")
parser.add_argument("--key", help="OPENAI_API_KEY")
parser.add_argument("--number", type=int, help="Number of results to return")

args = parser.parse_args()

embedding = OpenAIEmbeddings(openai_api_key=args.key)
persist_directory = 'db'

# Now we can load the persisted database from disk, and use it as normal.
vectordb = Chroma(persist_directory=persist_directory, embedding_function=embedding)
similar_docs = vectordb.similarity_search(args.query, k=args.number)
for doc in similar_docs:
    print(doc.page_content)
