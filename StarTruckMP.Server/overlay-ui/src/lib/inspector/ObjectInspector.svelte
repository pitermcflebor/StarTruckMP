<script lang="ts">
    import {onGameMessage} from "$lib";
    import type {ObjectData} from "$lib/inspector/ObjectData";
    import Object from "$lib/inspector/Object.svelte";

    const serializeData = (data: unknown) => {
        try {
            return JSON.stringify(data, null, 2);
        } catch {
            return String(data);
        }
    };

    const copyToClipboard = async (data: unknown) => {
        const serializedData = serializeData(data);
        await navigator.clipboard.writeText(serializedData);
    };

    const exportData = (fileName: string, data: unknown) => {
        const serializedData = serializeData(data);
        const blob = new Blob([serializedData], {type: "application/json"});
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");

        link.href = url;
        link.download = fileName;
        link.click();

        URL.revokeObjectURL(url);
    };
    
    let objectData: ObjectData|undefined|null = $state();
    
    onGameMessage<ObjectData>("inspectObject", (data) => {
        objectData = data.payload;
    })
    
    let objectExtraData: any = $state();
    
    onGameMessage<any>("inspectObjectExtra", (data) => {
        objectExtraData = data.payload;
    });
</script>

{#if (objectData)}
    <div class="object-inspector-container">
        <div class="inspector-panel">
            <div class="inspector-toolbar">
                <button type="button" onclick={() => copyToClipboard(objectData)}>Copy</button>
                <button type="button" onclick={() => exportData("object-data.json", objectData)}>Export</button>
            </div>
            <div class="object-inspector">
                <Object {...objectData} />
            </div>
        </div>
        {#if (objectExtraData)}
        <div class="inspector-panel">
            <div class="inspector-toolbar">
                <button type="button" onclick={() => copyToClipboard(objectExtraData)}>Copy</button>
                <button type="button" onclick={() => exportData("object-extra-data.json", objectExtraData)}>Export</button>
            </div>
            <div class="object-inspector-extra">
                <code>
                    {serializeData(objectExtraData)}
                </code>
            </div>
        </div>
        {/if}
    </div>
{/if}

<style>
    .object-inspector-container {
        position: absolute;
        top: 1vh;
        left: 50vw;
        transform: translateX(-50%);
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 1vh;
    }

    .inspector-panel {
        display: flex;
        flex-direction: column;
        gap: 0.5vh;
    }

    .inspector-toolbar {
        display: flex;
        justify-content: flex-end;
        gap: 0.5vw;
    }

    .inspector-toolbar button {
        border: 1px solid rgb(255 255 255 / 20%);
        background-color: rgb(0 0 0 / 65%);
        color: white;
        cursor: pointer;
        padding: 0.35rem 0.75rem;
    }

    .inspector-toolbar button:hover {
        background-color: rgb(255 255 255 / 12%);
    }

    .object-inspector {
        width: auto;
        height: auto;
        max-height: 15vh;
        overflow: auto;
        background-color: rgb(0 0 0 / 50%);
    }
    
    .object-inspector-extra {
        width: auto;
        height: auto;
        max-height: 30vh;
        overflow: auto;
        background-color: rgb(0 0 0 / 50%);
        white-space: pre-wrap;
    }
</style>