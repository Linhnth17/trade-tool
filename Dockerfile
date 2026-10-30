# ── Build ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BtcSignalBot.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish BtcSignalBot.csproj -c Release -o /app --no-restore

# ── Run ──
FROM mcr.microsoft.com/dotnet/runtime:8.0
# fonts: ClosedXML cần font để tự canh độ rộng cột Excel; tzdata: hiển thị giờ Việt Nam
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu-core tzdata \
 && rm -rf /var/lib/apt/lists/*
ENV TZ=Asia/Ho_Chi_Minh \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
COPY --from=build /app .

# Render/Railway/Fly tự cấp biến PORT; chạy local thì mặc định 8080
ENV PORT=8080
EXPOSE 8080

# Chạy liên tục không nghỉ, mọi lệnh ghi vào một file reports/BTCUSDT.P_5m_all.xlsx.
# Đổi tham số ở đây (hoặc "Docker Command" trên Render) nếu muốn: nến, vốn, tiền mỗi lệnh, đòn bẩy...
ENTRYPOINT ["dotnet", "BtcSignalBot.dll"]
CMD ["BTCUSDT.P", "5m", "--continuous", "--capital", "1000", "--stake", "10", "--leverage", "100", "--headless"]
