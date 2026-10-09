(() => {
    const joinPanel = document.getElementById("join-panel");
    const joinForm = document.getElementById("join-form");
    const joinError = document.getElementById("join-error");
    const nameInput = document.getElementById("name-input");
    const messageForm = document.getElementById("message-form");
    const messageInput = document.getElementById("message-input");
    const messageList = document.getElementById("message-list");
    const fileInput = document.getElementById("file-input");
    const emojiButton = document.getElementById("emoji-button");
    const emojiPicker = document.getElementById("emoji-picker");
    const uploadList = document.getElementById("upload-list");
    const userList = document.getElementById("user-list");
    const userCount = document.getElementById("user-count");
    const statusLabel = document.getElementById("connection-label");
    const statusIndicator = document.getElementById("connection-indicator");
    const roomSubtitle = document.getElementById("room-subtitle");
    const composerHint = document.getElementById("composer-hint");

    let socket;
    let displayName = "";
    let sessionToken = "";

    function setStatus(label, state) {
        statusLabel.textContent = label;
        statusIndicator.className = `status-dot${state ? ` ${state}` : ""}`;
    }

    function connect(name) {
        displayName = name;
        joinError.textContent = "";
        setStatus("Connecting…", "connecting");
        roomSubtitle.textContent = "Connecting to the chat room…";
        joinForm.querySelector("button").disabled = true;
        socket = new WebSocket(`${location.protocol === "https:" ? "wss:" : "ws:"}//${location.host}/ws`);

        socket.addEventListener("open", () => {
            socket.send(JSON.stringify({ type: "join", name: displayName }));
        });
        socket.addEventListener("message", event => {
            let message;
            try {
                message = JSON.parse(event.data);
            } catch {
                setStatus("Received an invalid server response", "");
                return;
            }

            switch (message.type) {
                case "joined":
                    sessionToken = message.sessionToken;
                    joinPanel.hidden = true;
                    messageForm.hidden = false;
                    composerHint.hidden = false;
                    messageInput.focus();
                    setStatus("Connected", "online");
                    roomSubtitle.textContent = "Messages are shared with everyone in the room";
                    for (const item of message.history ?? []) renderMessage(item);
                    break;
                case "message":
                    renderMessage(message.message);
                    break;
                case "system":
                    renderSystemMessage(message.text);
                    break;
                case "presence":
                    renderUsers(message.users ?? []);
                    break;
                case "error":
                    joinError.textContent = message.text;
                    setStatus("Could not join", "");
                    joinForm.querySelector("button").disabled = false;
                    break;
            }
        });
        socket.addEventListener("close", () => {
            sessionToken = "";
            messageForm.hidden = true;
            composerHint.hidden = true;
            joinPanel.hidden = false;
            joinForm.querySelector("button").disabled = false;
            setStatus("Disconnected", "");
            roomSubtitle.textContent = "Connect to join the conversation";
        });
        socket.addEventListener("error", () => {
            joinError.textContent = "Could not connect to the chat server. Please try again.";
            setStatus("Connection failed", "");
        });
    }

    function scrollToLatest() {
        messageList.scrollTop = messageList.scrollHeight;
    }

    function renderSystemMessage(text) {
        const item = document.createElement("div");
        item.className = "system-message";
        item.textContent = text;
        messageList.append(item);
        scrollToLatest();
    }

    function renderMessage(message) {
        if (!message || (message.type !== "chat" && message.type !== "file")) return;

        const article = document.createElement("article");
        article.className = `message${message.sender === displayName ? " mine" : ""}`;

        const avatar = document.createElement("div");
        avatar.className = "message-avatar";
        avatar.textContent = initials(message.sender);
        article.append(avatar);

        const content = document.createElement("div");
        content.className = "message-content";
        const meta = document.createElement("div");
        meta.className = "message-meta";
        const sender = document.createElement("span");
        sender.className = "message-sender";
        sender.textContent = message.sender;
        const time = document.createElement("time");
        time.className = "message-time";
        time.textContent = formatTime(message.time);
        meta.append(sender, time);
        content.append(meta);

        const bubble = document.createElement("div");
        bubble.className = "message-bubble";
        if (message.type === "chat") {
            bubble.textContent = message.text;
        } else {
            renderFile(message, bubble);
        }

        content.append(bubble);
        article.append(content);
        messageList.append(article);
        scrollToLatest();
    }

    function renderFile(message, container) {
        if (message.isImage) {
            const image = document.createElement("img");
            image.className = "shared-image";
            image.src = `/files/${encodeURIComponent(message.fileId)}`;
            image.alt = message.fileName;
            image.loading = "lazy";
            container.append(image);
        }

        const card = document.createElement("div");
        card.className = "file-card";
        const icon = document.createElement("span");
        icon.className = "file-icon";
        icon.textContent = message.isImage ? "▧" : "↗";
        const details = document.createElement("div");
        details.className = "file-details";
        const fileName = document.createElement("span");
        fileName.className = "file-name";
        fileName.textContent = message.fileName;
        fileName.title = message.fileName;
        const fileSize = document.createElement("div");
        fileSize.className = "file-size";
        fileSize.textContent = formatSize(message.fileSize);
        const download = document.createElement("a");
        download.className = "download-link";
        download.href = `/files/${encodeURIComponent(message.fileId)}/download`;
        download.textContent = "Download file";
        details.append(fileName, fileSize, download);
        card.append(icon, details);
        container.append(card);
    }

    function renderUsers(users) {
        userList.replaceChildren();
        for (const name of users) {
            const item = document.createElement("li");
            const avatar = document.createElement("span");
            avatar.className = "user-avatar";
            avatar.textContent = initials(name);
            const label = document.createElement("span");
            label.textContent = name;
            const online = document.createElement("span");
            online.className = "user-online-dot";
            online.setAttribute("aria-label", "Online");
            item.append(avatar, label, online);
            userList.append(item);
        }
        userCount.textContent = String(users.length);
        roomSubtitle.textContent = `${users.length} ${users.length === 1 ? "person" : "people"} in the room`;
    }

    function initials(name) {
        const parts = (name || "?").trim().split(/\s+/).slice(0, 2);
        return parts.map(part => Array.from(part)[0] ?? "").join("").toUpperCase();
    }

    function formatTime(value) {
        const date = new Date(value);
        return Number.isNaN(date.getTime())
            ? ""
            : new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(date);
    }

    function formatSize(size) {
        if (!size) return "Empty file";
        const units = ["B", "KB", "MB", "GB"];
        const index = Math.min(Math.floor(Math.log(size) / Math.log(1024)), units.length - 1);
        return `${(size / (1024 ** index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
    }

    function uploadFile(file) {
        const item = document.createElement("div");
        item.className = "upload-item";
        const label = document.createElement("span");
        label.className = "upload-name";
        label.textContent = `Uploading ${file.name}`;
        const percentage = document.createElement("span");
        percentage.textContent = "0%";
        const progress = document.createElement("progress");
        progress.className = "upload-progress";
        progress.max = 100;
        progress.value = 0;
        item.append(label, percentage, progress);
        uploadList.append(item);

        return new Promise(resolve => {
            const xhr = new XMLHttpRequest();
            xhr.open("POST", `/api/files?name=${encodeURIComponent(file.name)}`);
            xhr.setRequestHeader("X-Chat-Session", sessionToken);
            xhr.setRequestHeader("Content-Type", "application/octet-stream");
            xhr.upload.addEventListener("progress", event => {
                if (!event.lengthComputable) return;
                const value = Math.round((event.loaded / event.total) * 100);
                progress.value = value;
                percentage.textContent = `${value}%`;
            });
            xhr.addEventListener("load", () => {
                if (xhr.status >= 200 && xhr.status < 300) {
                    label.textContent = `Shared ${file.name}`;
                    progress.value = 100;
                    percentage.textContent = "Done";
                    resolve();
                } else {
                    showUploadError(item, label, file.name, readError(xhr.responseText) || `Upload failed (${xhr.status}).`);
                    resolve();
                }
            });
            xhr.addEventListener("error", () => {
                showUploadError(item, label, file.name, "Network error while uploading.");
                resolve();
            });
            xhr.addEventListener("abort", () => {
                showUploadError(item, label, file.name, "Upload was canceled.");
                resolve();
            });
            xhr.send(file);
        });
    }

    function readError(body) {
        try {
            return JSON.parse(body).error;
        } catch {
            return "";
        }
    }

    function showUploadError(item, label, fileName, error) {
        label.textContent = `Could not upload ${fileName}: ${error}`;
        label.classList.add("upload-error");
        item.querySelector("progress")?.remove();
        item.querySelector("span:last-of-type")?.remove();
    }

    joinForm.addEventListener("submit", event => {
        event.preventDefault();
        const name = nameInput.value.trim();
        if (!name) {
            joinError.textContent = "Enter a name to join the chat.";
            return;
        }
        connect(name);
    });

    messageForm.addEventListener("submit", event => {
        event.preventDefault();
        const text = messageInput.value.trim();
        if (!text || !socket || socket.readyState !== WebSocket.OPEN) return;
        socket.send(JSON.stringify({ type: "send", text }));
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

        const start = messageInput.selectionStart;
        const end = messageInput.selectionEnd;
        const emoji = button.dataset.emoji;
        messageInput.setRangeText(emoji, start, end, "end");
        messageInput.focus();
        emojiPicker.hidden = true;
        emojiButton.setAttribute("aria-expanded", "false");
    });
    fileInput.addEventListener("change", async () => {
        const files = Array.from(fileInput.files ?? []);
        fileInput.value = "";
        for (const file of files) await uploadFile(file);
    });
})();
