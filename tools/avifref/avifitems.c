// Prints libavif 1.4.2's view of an AVIF file's HEIF structure as JSON: the ftyp brands, the primary item, every item of the
// root meta box with its type, extents, properties in association order and the references libavif keeps, and the entity
// groups. Built in the egami-avifref image against libavif's own read.c (BSD-2), whose item model is private, so the Phase 2.9.2
// container reader can be compared with libavif's on the corpus. Run as an oracle only.
//
// Usage: avifitems file.avif
#include "../src/read.c"

// scale.c needs libyuv; parsing never scales.
avifResult avifImageScaleWithLimit(avifImage * image,
                                   uint32_t dstWidth,
                                   uint32_t dstHeight,
                                   uint32_t imageSizeLimit,
                                   uint32_t imageDimensionLimit,
                                   avifDiagnostics * diag)
{
    (void)image, (void)dstWidth, (void)dstHeight, (void)imageSizeLimit, (void)imageDimensionLimit, (void)diag;
    return AVIF_RESULT_NOT_IMPLEMENTED;
}

static void printFourCC(const uint8_t type[4])
{
    putchar('"');
    for (int i = 0; i < 4; ++i) {
        uint8_t c = type[i];
        if (c >= 0x20 && c < 0x7F && c != '"' && c != '\\') {
            putchar(c);
        } else {
            printf("\\u%04x", c);
        }
    }
    putchar('"');
}

int main(int argc, char * argv[])
{
    if (argc != 2) {
        fprintf(stderr, "Usage: avifitems file.avif\n");
        return 2;
    }
    avifDecoder * decoder = avifDecoderCreate();
    decoder->strictFlags = AVIF_STRICT_DISABLED;
    if (avifDecoderSetIOFile(decoder, argv[1]) != AVIF_RESULT_OK) {
        fprintf(stderr, "Cannot open %s\n", argv[1]);
        return 2;
    }
    avifResult result = avifDecoderParse(decoder);
    avifDecoderData * data = decoder->data;
    printf("{\"result\": \"%s\"", avifResultToString(result));
    if (result != AVIF_RESULT_OK) {
        printf(", \"diagnostics\": \"");
        for (const char * c = decoder->diag.error; *c; ++c) {
            if (*c == '"' || *c == '\\') {
                putchar('\\');
            }
            putchar(*c >= 0x20 ? *c : ' ');
        }
        putchar('"');
    }
    if (data) {
        printf(", \"major_brand\": ");
        printFourCC(data->majorBrand);
        printf(", \"compatible_brands\": [");
        for (uint32_t i = 0; i < data->compatibleBrands.count; ++i) {
            printf(i ? ", " : "");
            printFourCC(data->compatibleBrands.brand[i]);
        }
        printf("]");
    }
    if (data && data->meta) {
        const avifMeta * meta = data->meta;
        printf(", \"primary_item\": %u, \"idat_size\": %zu, \"items\": [", meta->primaryItemID, meta->idat.size);
        for (uint32_t i = 0; i < meta->items.count; ++i) {
            const avifDecoderItem * item = meta->items.item[i];
            printf("%s\n  {\"id\": %u, \"type\": ", i ? "," : "", item->id);
            printFourCC(item->type);
            printf(", \"size\": %zu, \"idat\": %s, \"extents\": [", item->size, item->idatStored ? "true" : "false");
            for (uint32_t e = 0; e < item->extents.count; ++e) {
                printf("%s[%" PRIu64 ", %zu]", e ? ", " : "", item->extents.extent[e].offset, item->extents.extent[e].size);
            }
            printf("], \"properties\": [");
            for (uint32_t p = 0; p < item->properties.count; ++p) {
                printf(p ? ", " : "");
                printFourCC(item->properties.prop[p].type);
            }
            printf("], \"content_type\": \"%s\"", item->contentType.contentType);
            printf(", \"thumbnail_for\": %u, \"aux_for\": %u, \"desc_for\": %u, \"dimg_for\": %u, \"dimg_index\": %u", item->thumbnailForID,
                   item->auxForID, item->descForID, item->dimgForID, item->dimgIdx);
            printf(", \"has_dimg_from\": %s, \"prem_by\": %u, \"unsupported_essential\": %s, \"progressive\": %s}",
                   item->hasDimgFrom ? "true" : "false", item->premByID, item->hasUnsupportedEssentialProperty ? "true" : "false",
                   item->progressive ? "true" : "false");
        }
        printf("\n], \"groups\": [");
        for (uint32_t g = 0; g < meta->entityToGroups.count; ++g) {
            const avifEntityToGroup * group = &meta->entityToGroups.groups[g];
            printf("%s{\"type\": ", g ? ", " : "");
            printFourCC(group->groupingType);
            printf(", \"id\": %u, \"entities\": [", group->groupID);
            for (uint32_t e = 0; e < group->entityIDs.count; ++e) {
                printf("%s%u", e ? ", " : "", group->entityIDs.ids[e]);
            }
            printf("]}");
        }
        printf("]");
    }
    printf("}\n");
    avifDecoderDestroy(decoder);
    return 0;
}
