(function () {
    const chatMessages = document.getElementById('chatMessages');
    const chatForm = document.getElementById('chatForm');
    const questionInput = document.getElementById('questionInput');
    const sendBtn = document.getElementById('sendBtn');

    // Auto-resize textarea
    questionInput.addEventListener('input', function () {
        this.style.height = 'auto';
        this.style.height = Math.min(this.scrollHeight, 120) + 'px';
    });

    // Submit on Enter (Shift+Enter for newline)
    questionInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            chatForm.dispatchEvent(new Event('submit'));
        }
    });

    chatForm.addEventListener('submit', async function (e) {
        e.preventDefault();
        const question = questionInput.value.trim();
        if (!question) return;

        // Clear welcome message on first question
        const welcome = chatMessages.querySelector('.welcome-message');
        if (welcome) welcome.remove();

        appendMessage('user', question);
        questionInput.value = '';
        questionInput.style.height = 'auto';
        sendBtn.disabled = true;

        const typingEl = showTypingIndicator();

        try {
            const response = await fetch('/ask', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ question: question })
            });

            typingEl.remove();

            if (!response.ok) {
                const errorText = await response.text();
                appendMessage('error', 'Something went wrong. Please try again.');
                console.error('API error:', errorText);
                return;
            }

            const data = await response.json();
            appendMessage('assistant', data.answer || 'No answer returned.');
        } catch (err) {
            typingEl.remove();
            appendMessage('error', 'Unable to connect. Please check your connection and try again.');
            console.error('Network error:', err);
        } finally {
            sendBtn.disabled = false;
            questionInput.focus();
        }
    });

    function appendMessage(role, content) {
        const messageDiv = document.createElement('div');
        messageDiv.className = 'message ' + role;

        const avatar = document.createElement('div');
        avatar.className = 'message-avatar';
        avatar.textContent = role === 'user' ? 'U' : role === 'error' ? '!' : 'AI';

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';
        contentDiv.textContent = content;

        messageDiv.appendChild(avatar);
        messageDiv.appendChild(contentDiv);
        chatMessages.appendChild(messageDiv);
        chatMessages.scrollTop = chatMessages.scrollHeight;
    }

    function showTypingIndicator() {
        const messageDiv = document.createElement('div');
        messageDiv.className = 'message assistant';

        const avatar = document.createElement('div');
        avatar.className = 'message-avatar';
        avatar.textContent = 'AI';

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';

        const typing = document.createElement('div');
        typing.className = 'typing-indicator';
        typing.innerHTML = '<span></span><span></span><span></span>';

        contentDiv.appendChild(typing);
        messageDiv.appendChild(avatar);
        messageDiv.appendChild(contentDiv);
        chatMessages.appendChild(messageDiv);
        chatMessages.scrollTop = chatMessages.scrollHeight;

        return messageDiv;
    }

    // Expose for suggested chips
    window.askSuggested = function (question) {
        questionInput.value = question;
        chatForm.dispatchEvent(new Event('submit'));
    };
})();
