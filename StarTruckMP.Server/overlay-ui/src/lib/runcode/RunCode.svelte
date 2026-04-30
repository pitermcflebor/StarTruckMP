<script lang="ts">
    import {onGameMessage, sendToGame} from "$lib";

    interface Response {
        status: number;
        logs: Array<string>;
    }
    
    let response: any | null = $state(null);
    
    onGameMessage<any>("runcodeResponse", (data) => {
        response = data.payload || null;
    });
    
    let code: string = $state("Console.WriteLine(\"Hello World\");");
    
    function runCode() {
        sendToGame("runcode", code);
    }
    
    function clear() {
        response = null;
    }
</script>

<div class="editor">
    <div class="editor-code">
        <textarea bind:value={code} placeholder="Enter your code here...">
        </textarea>
    </div>
    <div class="editor-console">
        {#if response}
            {#if response.status == 1}
                <div>Result ({response.logs.length}):</div>
            {#each response.logs as log}
                <div class="console-line">
                    <span>&gt;</span>
                    <span>{log}</span>
                </div>
            {/each}
            {/if}
            {#if response.status == 0}
                <div>Exception thrown</div>
            {/if}
        {/if}
        {#if response == null}
            <div>Waiting run</div>
        {/if}
    </div>
    <div class="editor-buttons">
        <button onclick={runCode}>Run</button>
        <button onclick={clear}>Clear</button>
    </div>
</div>

<style>
    .editor {
        position: absolute;
        top: 25vh;
        left: 3vw;
        width: 25vw;
        min-height: 30vh;
        max-height: 60vh;
        overflow: auto;
        display: flex;
        flex-direction: column;
        
        background-color: rgb(0 0 0 / 50%);
        
        .editor-code {
            width: 100%;
            height: 100%;
            overflow: auto;
            
            textarea {
                width: 98%;
                max-width: 98%;
                height: 100%;
                overflow: auto;
                background-color: transparent;
                color: yellow;
            }
        }
        
        .editor-console {
            width: 100%;
            max-height: 100%;
            overflow: auto;
        }
    }
</style>