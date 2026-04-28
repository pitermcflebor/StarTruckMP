<script lang="ts">
    import type {ObjectData} from "$lib/inspector/ObjectData";
    import Self from "$lib/inspector/Object.svelte";
    import {sendToGame} from "$lib";
    
    let {id, name, type, children} = $props<{
        name: string;
        type: string;
        children: ObjectData[];
    }>();
    
    function requestDetails() {
        sendToGame("inspectObjectExtra", id)
    }
</script>

<div class="object">
    <div class="name" onclick={requestDetails}>{name}</div>
    <div class="type">{type}</div>
    {#if (children && children.length > 0)}
        <div class="child-objects">
            {#each children as child}
                <Self {...child} />
            {/each}
        </div>
    {/if}
</div>

<style>
    .object {
        width: 100%;
        
        &:hover {
            > .name {
                cursor: pointer;
                font-weight: bold;
            }
        }

        .name,
        .type {
            display: inline-block;
        }

        .child-objects {
            display: flex;
            flex-direction: row;
            flex-wrap: wrap;
            padding-left: 10px;
            border-top: 1px solid white;
        }
    }
</style>