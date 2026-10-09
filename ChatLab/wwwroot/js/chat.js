(() => {
    const account = document.querySelector(".account-name");
    const currentUserId = account.dataset.userId;
    const displayName = account.textContent.trim();
    const socket = new WebSocket(`${location.protocol === "https:" ? "wss:" : "ws:"}//${location.host}/ws`);
    const messageList = document.getElementById("message-list");
    const messageForm = document.getElementById("message-form");
    const messageInput = document.getElementById("message-input");
    const conversationList = document.getElementById("conversation-list");
    const userList = document.getElementById("user-list");
    const groupDialog = document.getElementById("group-dialog");
    const groupMemberList = document.getElementById("group-member-list");
    const groupError = document.getElementById("group-error");
    const emptyState = document.getElementById("empty-state");
    const statusLabel = document.getElementById("connection-label");
    const statusIndicator = document.getElementById("connection-indicator");
    const fileInput = document.getElementById("file-input");
    const emojiButton = document.getElementById("emoji-button");
    const emojiPicker = document.getElementById("emoji-picker");
    const ratesPanel = document.getElementById("rates-panel");
    const rateTableBody = document.getElementById("rate-table-body");
    const rateStatistics = document.getElementById("rate-statistics");
    const ratesLiveStatus = document.getElementById("rates-live-status");
    let activeConversationId = null;
    let activeView = "chat";
    let conversations = [];
    let directory = [];
    let visibleRates = [];
    let showingHistory = false;

    function send(command) {
        if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(command));
    }

    function setStatus(text, state = "") {
        statusLabel.textContent = text;
        statusIndicator.className = `status-dot${state ? ` ${state}` : ""}`;
    }

    socket.addEventListener("open", () => send({ type: "join" }));
    socket.addEventListener("message", event => {
        let message;
        try {
            message = JSON.parse(event.data);
        } catch {
            setStatus("Invalid server response");
            return;
        }

        switch (message.type) {
            case "joined":
                setStatus("Connected", "online");
                renderUsers(message.users ?? []);
                renderConversations(message.conversations ?? []);
                break;
            case "directory":
                renderUsers(message.users ?? []);
                break;
            case "conversations":
                renderConversations(message.conversations ?? []);
                break;
            case "history":
                activeConversationId = message.conversationId;
                updateActiveConversation();
                showChatView();
                messageList.replaceChildren();
                messageList.hidden = false;
                emptyState.hidden = true;
                messageForm.hidden = false;
                document.getElementById("composer-hint").hidden = false;
                for (const item of message.messages ?? []) renderMessage(item);
                break;
            case "message":
                if (message.message.conversationId === activeConversationId) renderMessage(message.message);
                break;
            case "error":
                groupError.textContent = message.text;
                break;
        }
    });
    socket.addEventListener("close", () => {
        setStatus("Disconnected");
        messageForm.hidden = true;
        document.getElementById("composer-hint").hidden = true;
    });
    socket.addEventListener("error", () => setStatus("Connection failed"));

    const rateHub = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/exchange-rates")
        .withAutomaticReconnect()
        .build();
    rateHub.on("RatesUpdated", rates => {
        if (activeView === "rates" && !showingHistory) {
            visibleRates = rates;
            renderRates(visibleRates);
            loadRateStatistics(document.getElementById("rate-date").value);
        }
        setRatesStatus("Live updates connected", "online");
    });
    rateHub.onreconnecting(() => setRatesStatus("Reconnecting…"));
    rateHub.onreconnected(() => setRatesStatus("Live updates connected", "online"));
    rateHub.onclose(() => setRatesStatus("Live updates disconnected", "error"));
    rateHub.start()
        .then(() => setRatesStatus("Live updates connected", "online"))
        .catch(error => {
            console.error("Could not connect to the exchange-rate hub.", error);
            setRatesStatus("Live updates unavailable", "error");
        });

    function setRatesStatus(text, state = "") {
        ratesLiveStatus.textContent = text;
        ratesLiveStatus.className = `rates-live-status${state ? ` ${state}` : ""}`;
    }

    async function fetchJson(url) {
        const response = await fetch(url, { headers: { Accept: "application/json" } });
        if (!response.ok) throw new Error(`Request failed (${response.status}).`);
        return response.json();
    }

    async function loadLatestRates() {
        setRatesStatus("Loading sample rates…");
        try {
            showingHistory = false;
            visibleRates = await fetchJson("/api/rates/latest");
            renderRates(visibleRates);
            await loadRateStatistics(document.getElementById("rate-date").value);
            const connected = rateHub.state === signalR.HubConnectionState.Connected;
            setRatesStatus(connected ? "Live updates connected" : "Sample rates loaded", "online");
        } catch (error) {
            console.error("Could not load exchange rates.", error);
            setRatesStatus("Rates could not be loaded", "error");
        }
    }

    async function loadRateStatistics(date) {
        const query = date ? `?date=${encodeURIComponent(date)}` : "";
        try {
            const statistics = await fetchJson(`/api/rates/statistics${query}`);
            rateStatistics.replaceChildren();
            for (const item of statistics) {
                const statistic = document.createElement("div");
                statistic.className = "rate-statistic";
                const title = document.createElement("span");
                title.textContent = `${item.currencyCode} average`;
                const average = document.createElement("strong");
                average.textContent = `Buy ${formatRate(item.averageBuyRate)} · Sell ${formatRate(item.averageSellRate)}`;
                statistic.append(title, average);
                rateStatistics.append(statistic);
            }
        } catch (error) {
            console.error("Could not load exchange-rate statistics.", error);
            rateStatistics.textContent = "Daily averages are unavailable.";
        }
    }

    function formatRate(value) {
        return Number(value).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }

    function renderRates(rates) {
        visibleRates = rates ?? [];
        rateTableBody.replaceChildren();
        document.getElementById("rate-empty").hidden = visibleRates.length > 0;
        for (let index = 0; index < visibleRates.length; index++) {
            const rate = visibleRates[index];
            const row = document.createElement("tr");
            const currencyCell = document.createElement("td");
            const currency = document.createElement("span");
            currency.className = "rate-code";
            currency.textContent = rate.currencyCode;
            const base = document.createElement("span");
            base.className = "rate-base";
            base.textContent = `/ ${rate.baseCurrency}`;
            currencyCell.append(currency, base);
            const buy = document.createElement("td");
            buy.textContent = formatRate(rate.buyRate);
            const sell = document.createElement("td");
            sell.textContent = formatRate(rate.sellRate);
            const updated = document.createElement("td");
            updated.textContent = new Intl.DateTimeFormat(undefined, {
                dateStyle: "medium",
                timeStyle: "short"
            }).format(new Date(rate.timestamp));
            const actionCell = document.createElement("td");
            const share = document.createElement("button");
            share.type = "button";
            share.className = "rate-share-button";
            share.dataset.rateIndex = String(index);
            share.textContent = "Share to chat";
            share.disabled = !activeConversationId;
            share.title = activeConversationId ? "Share this sample rate in the selected chat" : "Select a conversation first";
            actionCell.append(share);
            row.append(currencyCell, buy, sell, updated, actionCell);
            rateTableBody.append(row);
        }
    }

    async function loadCurrencies() {
        try {
            const currencies = await fetchJson("/api/rates/currencies");
            const select = document.getElementById("rate-currency");
            for (const currency of currencies) {
                const option = document.createElement("option");
                option.value = currency;
                option.textContent = currency;
                select.append(option);
            }
        } catch (error) {
            console.error("Could not load available currencies.", error);
            setRatesStatus("Currency list unavailable", "error");
        }
    }

    const today = new Date();
    document.getElementById("rate-date").value =
        `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, "0")}-${String(today.getDate()).padStart(2, "0")}`;
    loadLatestRates();
    loadCurrencies();

    function initials(name) {
        return (name || "?").trim().split(/\s+/).slice(0, 2)
            .map(part => Array.from(part)[0] ?? "").join("").toUpperCase();
    }

    function renderUsers(users) {
        directory = users;
        userList.replaceChildren();
        for (const user of users) {
            if (user.id === currentUserId) continue;
            const item = document.createElement("li");
            item.className = "directory-item";
            item.dataset.userId = user.id;
            item.tabIndex = 0;
            item.setAttribute("role", "button");
            const avatar = document.createElement("span");
            avatar.className = "user-avatar";
            avatar.textContent = initials(user.name);
            const name = document.createElement("span");
            name.className = "directory-name";
            name.textContent = user.name;
            const status = document.createElement("span");
            status.className = `user-online-dot${user.isOnline ? "" : " offline"}`;
            status.title = user.isOnline ? "Online" : "Offline";
            item.append(avatar, name, status);
            userList.append(item);
        }
        document.getElementById("user-count").textContent = String(Math.max(0, users.length - 1));
        renderGroupMembers();
    }

    function renderConversations(items) {
        conversations = items;
        conversationList.replaceChildren();
        for (const conversation of items) {
            const item = document.createElement("li");
            item.className = `conversation-item${conversation.id === activeConversationId ? " selected" : ""}`;
            item.dataset.conversationId = conversation.id;
            item.tabIndex = 0;
            item.setAttribute("role", "button");
            const avatar = document.createElement("span");
            avatar.className = "user-avatar";
            avatar.textContent = conversation.isGroup ? "G" : initials(conversation.name);
            const details = document.createElement("span");
            details.className = "conversation-details";
            const title = document.createElement("span");
            title.className = "directory-name";
            title.textContent = conversation.name;
            const preview = document.createElement("span");
            preview.className = "conversation-preview";
            preview.textContent = conversation.lastMessage || (conversation.isGroup ? "Private group" : "Start a conversation");
            details.append(title, preview);
            item.append(avatar, details);
            conversationList.append(item);
        }
    }

    function updateActiveConversation() {
        const conversation = conversations.find(item => item.id === activeConversationId);
        document.getElementById("room-title").textContent = conversation?.name ?? "Conversation";
        document.getElementById("room-subtitle").textContent =
            conversation?.isGroup ? "Private group conversation" : "Direct message";
        renderConversations(conversations);
        renderRates(visibleRates);
    }

    function renderGroupMembers() {
        groupMemberList.replaceChildren();
        for (const user of directory) {
            if (user.id === currentUserId) continue;
            const label = document.createElement("label");
            label.className = "group-member";
            const checkbox = document.createElement("input");
            checkbox.type = "checkbox";
            checkbox.value = user.id;
            const name = document.createElement("span");
            name.textContent = user.name;
            const status = document.createElement("small");
            status.textContent = user.isOnline ? "Online" : "Offline";
            label.append(checkbox, name, status);
            groupMemberList.append(label);
        }
    }

    function renderMessage(message) {
        if (!message || !["chat", "file"].includes(message.type)) return;
        const article = document.createElement("article");
        article.className = `message${message.sender === displayName ? " mine" : ""}`;
        const avatar = document.createElement("div");
        avatar.className = "message-avatar";
        avatar.textContent = initials(message.sender);
        const content = document.createElement("div");
        content.className = "message-content";
        const meta = document.createElement("div");
        meta.className = "message-meta";
        const sender = document.createElement("span");
        sender.className = "message-sender";
        sender.textContent = message.sender;
        const time = document.createElement("time");
        time.className = "message-time";
        time.textContent = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" })
            .format(new Date(message.time));
        meta.append(sender, time);
        const bubble = document.createElement("div");
        bubble.className = "message-bubble";
        if (message.type === "chat") {
            bubble.textContent = message.text;
        } else {
            if (message.isImage) {
                const image = document.createElement("img");
                image.className = "shared-image";
                image.src = `/files/${encodeURIComponent(message.fileId)}`;
                image.alt = message.fileName;
                bubble.append(image);
            }
            const link = document.createElement("a");
            link.className = "download-link";
            link.href = `/files/${encodeURIComponent(message.fileId)}/download`;
            link.textContent = `${message.fileName} · ${formatSize(message.fileSize)} · Download`;
            bubble.append(link);
        }
        content.append(meta, bubble);
        article.append(avatar, content);
        messageList.append(article);
        messageList.scrollTop = messageList.scrollHeight;
    }

    function formatSize(size) {
        if (!size) return "Empty file";
        const units = ["B", "KB", "MB", "GB"];
        const index = Math.min(Math.floor(Math.log(size) / Math.log(1024)), units.length - 1);
        return `${(size / 1024 ** index).toFixed(index ? 1 : 0)} ${units[index]}`;
    }

    function showChatView() {
        activeView = "chat";
        ratesPanel.hidden = true;
        emptyState.hidden = Boolean(activeConversationId);
        messageList.hidden = !activeConversationId;
        messageForm.hidden = !activeConversationId;
        document.getElementById("composer-hint").hidden = !activeConversationId;
        document.getElementById("upload-list").hidden = !activeConversationId;
        document.getElementById("create-group").hidden = false;
    }

    function openPeople() {
        showChatView();
        document.getElementById("conversation-section").hidden = true;
        document.getElementById("people-section").hidden = false;
        document.getElementById("rates-section").hidden = true;
        document.getElementById("show-people").classList.add("active");
        document.getElementById("show-chats").classList.remove("active");
        document.getElementById("show-rates").classList.remove("active");
    }

    function openChats() {
        showChatView();
        document.getElementById("conversation-section").hidden = false;
        document.getElementById("people-section").hidden = true;
        document.getElementById("rates-section").hidden = true;
        document.getElementById("show-chats").classList.add("active");
        document.getElementById("show-people").classList.remove("active");
        document.getElementById("show-rates").classList.remove("active");
    }

    function openRates() {
        activeView = "rates";
        document.getElementById("conversation-section").hidden = true;
        document.getElementById("people-section").hidden = true;
        document.getElementById("rates-section").hidden = false;
        document.getElementById("show-chats").classList.remove("active");
        document.getElementById("show-people").classList.remove("active");
        document.getElementById("show-rates").classList.add("active");
        document.getElementById("create-group").hidden = true;
        document.getElementById("room-title").textContent = "Exchange rates";
        document.getElementById("room-subtitle").textContent = "Sample buy and sell rates, updated automatically";
        emptyState.hidden = true;
        messageList.hidden = true;
        messageForm.hidden = true;
        document.getElementById("composer-hint").hidden = true;
        document.getElementById("upload-list").hidden = true;
        ratesPanel.hidden = false;
        loadLatestRates();
    }

    document.getElementById("show-chats").addEventListener("click", openChats);
    document.getElementById("show-people").addEventListener("click", openPeople);
    document.getElementById("show-rates").addEventListener("click", openRates);
    document.getElementById("rate-filter-form").addEventListener("submit", async event => {
        event.preventDefault();
        const date = document.getElementById("rate-date").value;
        const currency = document.getElementById("rate-currency").value;
        const query = new URLSearchParams();
        if (date) query.set("date", date);
        if (currency) query.set("currency", currency);
        setRatesStatus("Searching rate history…");
        try {
            visibleRates = await fetchJson(`/api/rates/history?${query}`);
            showingHistory = true;
            renderRates(visibleRates);
            await loadRateStatistics(date);
            setRatesStatus("Rate history loaded", "online");
        } catch (error) {
            console.error("Could not search exchange-rate history.", error);
            setRatesStatus("Rate history could not be loaded", "error");
        }
    });
    document.getElementById("clear-rate-filter").addEventListener("click", () => {
        const now = new Date();
        document.getElementById("rate-date").value =
            `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
        document.getElementById("rate-currency").value = "";
        loadLatestRates();
    });
    rateTableBody.addEventListener("click", event => {
        const button = event.target.closest("[data-rate-index]");
        if (!button || !activeConversationId) return;
        const rate = visibleRates[Number(button.dataset.rateIndex)];
        if (!rate) return;
        const timestamp = new Intl.DateTimeFormat(undefined, {
            dateStyle: "medium",
            timeStyle: "short"
        }).format(new Date(rate.timestamp));
        const text = [
            `Sample exchange rate: 1 ${rate.currencyCode} = ${rate.baseCurrency}`,
            `Buy: ${formatRate(rate.buyRate)} ${rate.baseCurrency}`,
            `Sell: ${formatRate(rate.sellRate)} ${rate.baseCurrency}`,
            `Updated: ${timestamp}`,
            "Generated sample data; not a live financial quote."
        ].join("\n");
        send({ type: "send", conversationId: activeConversationId, text });
        openChats();
    });
    userList.addEventListener("click", event => {
        const item = event.target.closest("[data-user-id]");
        if (!item) return;
        send({ type: "openDirect", userId: item.dataset.userId });
        openChats();
    });
    userList.addEventListener("keydown", event => {
        if ((event.key === "Enter" || event.key === " ") && event.target.closest("[data-user-id]")) {
            event.preventDefault();
            event.target.closest("[data-user-id]").click();
        }
    });
    conversationList.addEventListener("click", event => {
        const item = event.target.closest("[data-conversation-id]");
        if (item) send({ type: "select", conversationId: item.dataset.conversationId });
    });
    conversationList.addEventListener("keydown", event => {
        if ((event.key === "Enter" || event.key === " ") && event.target.closest("[data-conversation-id]")) {
            event.preventDefault();
            event.target.closest("[data-conversation-id]").click();
        }
    });
    document.getElementById("create-group").addEventListener("click", () => {
        groupError.textContent = "";
        groupDialog.showModal();
    });
    document.getElementById("close-group").addEventListener("click", () => groupDialog.close());
    document.getElementById("cancel-group").addEventListener("click", () => groupDialog.close());
    document.getElementById("group-form").addEventListener("submit", event => {
        event.preventDefault();
        send({
            type: "createGroup",
            name: document.getElementById("group-name").value,
            memberIds: Array.from(groupMemberList.querySelectorAll("input:checked"), item => item.value)
        });
        groupDialog.close();
        document.getElementById("group-name").value = "";
    });
    messageForm.addEventListener("submit", event => {
        event.preventDefault();
        const text = messageInput.value.trim();
        if (!text || !activeConversationId) return;
        send({ type: "send", conversationId: activeConversationId, text });
        messageInput.value = "";
        messageInput.style.height = "40px";
    });
    messageInput.addEventListener("keydown", event => {
        if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            messageForm.requestSubmit();
        }
    });
    messageInput.addEventListener("input", () => {
        messageInput.style.height = "40px";
        messageInput.style.height = `${Math.min(messageInput.scrollHeight, 150)}px`;
    });
    document.getElementById("attach-button").addEventListener("click", () => fileInput.click());
    emojiButton.addEventListener("click", () => {
        emojiPicker.hidden = !emojiPicker.hidden;
        emojiButton.setAttribute("aria-expanded", String(!emojiPicker.hidden));
    });
    emojiPicker.addEventListener("click", event => {
        const button = event.target.closest("button[data-emoji]");
        if (!button) return;
        messageInput.setRangeText(button.dataset.emoji, messageInput.selectionStart, messageInput.selectionEnd, "end");
        messageInput.focus();
        emojiPicker.hidden = true;
        emojiButton.setAttribute("aria-expanded", "false");
    });
    fileInput.addEventListener("change", async () => {
        const files = Array.from(fileInput.files ?? []);
        fileInput.value = "";
        if (!activeConversationId) return;
        for (const file of files) await uploadFile(file);
    });

    function uploadFile(file) {
        const item = document.createElement("div");
        item.className = "upload-item";
        const label = document.createElement("span");
        label.className = "upload-name";
        label.textContent = `Uploading ${file.name}`;
        const progress = document.createElement("progress");
        progress.className = "upload-progress";
        progress.max = 100;
        item.append(label, progress);
        document.getElementById("upload-list").append(item);
        return new Promise(resolve => {
            const xhr = new XMLHttpRequest();
            xhr.open("POST", `/api/files?conversationId=${encodeURIComponent(activeConversationId)}&name=${encodeURIComponent(file.name)}`);
            xhr.setRequestHeader("X-CSRF-TOKEN", messageForm.querySelector('input[name="__RequestVerificationToken"]').value);
            xhr.setRequestHeader("Content-Type", "application/octet-stream");
            xhr.upload.addEventListener("progress", event => {
                if (event.lengthComputable) progress.value = event.loaded / event.total * 100;
            });
            xhr.addEventListener("load", () => {
                label.textContent = xhr.status >= 200 && xhr.status < 300
                    ? `Shared ${file.name}`
                    : `Could not upload ${file.name}`;
                progress.value = xhr.status >= 200 && xhr.status < 300 ? 100 : 0;
                resolve();
            });
            xhr.addEventListener("error", () => {
                label.textContent = `Network error uploading ${file.name}`;
                resolve();
            });
            xhr.send(file);
        });
    }
})();
