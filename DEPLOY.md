# Chạy bot 24/7 miễn phí trên Render

Bot chạy dạng Docker, dashboard có địa chỉ web công khai. Mỗi phiên 24h, hết phiên tự xuất Excel rồi mở phiên mới.

## 1. Đưa code lên GitHub

Tạo một repo (để **Private** cũng được) trên GitHub, rồi trong thư mục project:

```bash
git init
git add .
git commit -m "BTC signal bot"
git branch -M main
git remote add origin https://github.com/<tài-khoản>/<tên-repo>.git
git push -u origin main
```

## 2. Tạo service trên Render

1. Đăng nhập https://render.com bằng tài khoản GitHub.
2. **New → Blueprint** → chọn repo vừa tạo → **Apply**.
   Render đọc file `render.yaml`: Docker, gói **Free**, region **Frankfurt**.
3. Đợi build xong (lần đầu ~5 phút). Địa chỉ dashboard dạng `https://btc-signal-bot.onrender.com`.

> Không chọn region ở Mỹ (Oregon, Ohio, Virginia): Binance chặn IP Mỹ, bot sẽ báo lỗi 451.

## 3. Giữ bot không bị "ngủ"

Gói free của Render tự tắt app sau **15 phút không có ai truy cập** web. Bot tự kết nối ra Binance không tính là có người truy cập,
nên cần một dịch vụ ping miễn phí:

1. Đăng ký https://uptimerobot.com (free).
2. **Add New Monitor** → loại **HTTP(s)** → URL: `https://<tên-app>.onrender.com/health` → chu kỳ **5 phút**.

Gói free có 750 giờ/tháng, đủ cho 1 app chạy liên tục cả tháng (~744 giờ).

## 4. Đổi tham số

Sửa dòng `CMD` cuối `Dockerfile` rồi push lại (Render tự deploy lại), ví dụ:

```
CMD ["BTCUSDT.P", "5m", "--session", "24h", "--repeat", "--capital", "1000", "--stake", "10", "--leverage", "100", "--headless"]
```

Hoặc trên Render: service → **Settings → Docker Command**.

## Giới hạn của gói free

- **File Excel không giữ lâu dài**: mỗi lần app khởi động lại / deploy lại là mất. Tải về định kỳ ở trang `/reports`.
- Khởi động lại thì phiên đang chạy dở bị đóng và mở phiên mới.
- CPU yếu (0.1 vCPU): dashboard cập nhật mỗi 2 giây thay vì 0.5 giây.
- Ai có link đều xem được dashboard (không có mật khẩu). Bot chỉ đọc dữ liệu công khai, không có API key, không đặt lệnh thật.

## Xem log

Render → service → **Logs**: mỗi nến 5m đóng in một dòng (giá, trạng thái thị trường, tài sản phiên), mỗi lần vào/ra lệnh in thêm một dòng.
