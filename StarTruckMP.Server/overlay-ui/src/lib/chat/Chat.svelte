<script>
</script>

<div class="chat">
    <div class="chat-header">
        <div class="chat-tab selected">
            <span class="name">Local</span>
        </div>
        <div class="chat-tab">
            <span class="name">Global</span>
        </div>
    </div>
    <div class="chat-body">
        <div class="message">
            <div class="name">Foo Bar</div>
            <div class="text">Lorem ipsum dolor sit amet consectetur adipisicing elit. Illo, perspiciatis.</div>
        </div>
        <div class="message">
            <div class="name">Foo Bar</div>
            <div class="text">This is a test message!</div>
        </div>
        <div class="message">
            <div class="name">Foo Bar</div>
            <div class="text">This is a test message!</div>
        </div>
        <div class="message">
            <div class="name">Foo Bar</div>
            <div class="text">Lorem ipsum dolor sit amet consectetur adipisicing elit. Illo, perspiciatis.</div>
        </div>
        <div class="message">
            <div class="name">Foo Bar</div>
            <div class="text">This is a test message!</div>
        </div>
        <div class="message">
            <div class="name">Foo Bar</div>
            <div class="text">This is a test message!</div>
        </div>
    </div>
</div>

<style>
    .chat {
        --chat-header-height: 3vh;
        --chat-body-height: 10vh;

        position: absolute;
        right: .5vw;
        top: calc(50vh - var(--chat-body-height));
        width: 25vw;
        height: auto;
        background-color: rgb(239 200 6 / 50%);
        overflow: auto;
        padding: 5px;

        display: flex;
        flex-direction: column;

        .chat-header {
            height: var(--chat-header-height);
            display: flex;
            gap: 10px;

            .chat-tab {
                height: fit-content;
                padding: 0 5px;
            }

            .chat-tab.selected {
                background-color: rgb(0 0 0 / 20%);
            }
        }

        .chat-body {
            height: var(--chat-body-height);
            overflow: auto;

            .message {
                display: flex;

                .name {
                    font-size: 1rem;
                    flex-wrap: nowrap;
                    text-wrap: nowrap;

                    color: white;
                }

                .name::after {
                    content: ': ';
                    margin-right: 4pt;
                }
            }
        }
    }
</style>