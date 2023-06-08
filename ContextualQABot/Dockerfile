FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy
WORKDIR /app
COPY bin/Release/net6.0/publish .
COPY requirements.txt .

RUN apt-get update && apt-get install -y python3 python3-pip
RUN pip install --no-cache-dir -r requirements.txt
RUN apt-get install libchm-bin -y
RUN which python3

ENTRYPOINT ["dotnet", "ContextualQABot.dll"]
