import argparse
import yt_dlp

from langchain.embeddings import OpenAIEmbeddings
from langchain.vectorstores import Chroma
from langchain.text_splitter import CharacterTextSplitter
from langchain.document_loaders.generic import GenericLoader
from langchain.document_loaders.parsers import OpenAIWhisperParser

# create parser
parser = argparse.ArgumentParser()

# add arguments
parser.add_argument("--url", help="youtube url")

args = parser.parse_args()


def download_audio(video_url, output_dir):
    ydl_opts = {
        'format': 'bestaudio/best',
        'outtmpl': output_dir + '/%(title)s.%(ext)s',  # specify your output directory here
        'postprocessors': [{
            'key': 'FFmpegExtractAudio',
            'preferredcodec': 'mp3',
            'preferredquality': '192',
        }],
    }
    with yt_dlp.YoutubeDL(ydl_opts) as ydl:
        ydl.download([video_url])


download_audio(args.url, 'sources/')

loader = GenericLoader.from_filesystem('sources/', glob="*.mp3", parser=OpenAIWhisperParser())
documents = loader.load()

text_splitter = CharacterTextSplitter(chunk_size=1000, chunk_overlap=50, separator=" ")
texts = text_splitter.split_documents(documents=documents)

persist_directory = 'db'

embedding = OpenAIEmbeddings()
vectordb = Chroma.from_documents(documents=texts, embedding=embedding,
                                 persist_directory=persist_directory)
vectordb.persist()
vectordb = None
