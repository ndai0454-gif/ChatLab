// Shared SignalR connection. Pages subscribe with window.rateHub.onRates(callback).
(function () {
    const statusBadge = document.getElementById("connection-status");
    const handlers = [];

    function setStatus(text, cls) {
        if (!statusBadge) return;
        statusBadge.textContent = text;
        statusBadge.className = "badge " + cls;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/exchange-rates")
        .withAutomaticReconnect()
        .build();

    connection.on("RatesUpdated", rates => handlers.forEach(h => h(rates)));
    connection.onreconnecting(() => setStatus("Đang kết nối lại...", "text-bg-warning"));
    connection.onreconnected(() => setStatus("Real-time: bật", "text-bg-success"));
    connection.onclose(() => setStatus("Mất kết nối", "text-bg-danger"));

    connection.start()
        .then(() => setStatus("Real-time: bật", "text-bg-success"))
        .catch(() => setStatus("Mất kết nối", "text-bg-danger"));

    window.rateHub = {
        onRates: cb => handlers.push(cb),
        fmt: n => Number(n).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
        fmtTime: iso => iso.replace("T", " ").substring(0, 19),
    };
})();
