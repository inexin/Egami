/* hb-closure: the GSUB glyph closure as hb-subset computes it (HarfBuzz 14.5.1), for Egami's closure references.
 *
 * Usage: hb-closure FONT, then one request per line on stdin, one answer per line on stdout:
 *   N                      the face's glyph count
 *   M <text>               the nominal glyph of each code point of the UTF-8 text that the cmap maps, comma separated
 *   C <features> <gids>    the closure of the comma-separated glyph ids under <features> ("default" for hb-subset's default
 *                          layout features, "*" for all, "-" for none, or comma-separated tags), then "|", then every glyph
 *                          hb-subset's plan keeps with --gids=<gids> --retain-gids and the same features (which adds composite
 *                          components and other tables' closures to the GSUB closure)
 *
 * The closure repeats what hb-subset-plan-layout.cc does for GSUB: the features are collected from every script and language
 * system (hb_ot_layout_collect_features), all of them when the filter keeps every feature the table lists (_filter_tag_list),
 * then the lookups of those features and of their feature variations (hb_ot_layout_collect_lookups does the same), then
 * hb_ot_layout_lookups_substitute_closure.
 */
#include <hb.h>
#include <hb-ot.h>
#include <hb-subset.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void print_set (hb_set_t *set)
{
  hb_codepoint_t g = HB_SET_VALUE_INVALID;
  int first = 1;
  while (hb_set_next (set, &g))
  {
    printf (first ? "%u" : ",%u", g);
    first = 0;
  }
}

static void parse_gids (const char *s, hb_set_t *set)
{
  while (*s)
  {
    char *end;
    unsigned long v = strtoul (s, &end, 10);
    if (end == s)
      break;
    hb_set_add (set, (hb_codepoint_t) v);
    s = *end == ',' ? end + 1 : end;
  }
}

/* Fills the feature filter: hb-subset's defaults, everything, nothing or the listed tags. */
static void feature_filter (const char *spec, hb_set_t *filter)
{
  hb_set_clear (filter);
  if (!strcmp (spec, "default"))
  {
    hb_subset_input_t *input = hb_subset_input_create_or_fail ();
    hb_set_union (filter, hb_subset_input_set (input, HB_SUBSET_SETS_LAYOUT_FEATURE_TAG));
    hb_subset_input_destroy (input);
  }
  else if (!strcmp (spec, "*"))
    hb_set_invert (filter);
  else if (strcmp (spec, "-"))
  {
    const char *s = spec;
    while (*s)
    {
      const char *end = strchr (s, ',');
      size_t len = end ? (size_t) (end - s) : strlen (s);
      hb_set_add (filter, hb_tag_from_string (s, (int) len));
      s += len + (end ? 1 : 0);
    }
  }
}

static void closure (hb_face_t *face, hb_set_t *filter, hb_set_t *glyphs)
{
  unsigned count = hb_ot_layout_table_get_feature_tags (face, HB_OT_TAG_GSUB, 0, NULL, NULL);
  hb_tag_t *tags = calloc (count + 1, sizeof (hb_tag_t));
  hb_tag_t *kept = calloc (count + 1, sizeof (hb_tag_t));
  hb_ot_layout_table_get_feature_tags (face, HB_OT_TAG_GSUB, 0, &count, tags);
  hb_set_t *seen = hb_set_create ();
  int removed = 0;
  unsigned n = 0;
  for (unsigned i = 0; i < count; i++)
  {
    if (!tags[i] || hb_set_has (seen, tags[i]))
      continue;
    if (!hb_set_has (filter, tags[i]))
    {
      removed = 1;
      continue;
    }
    hb_set_add (seen, tags[i]);
    kept[n++] = tags[i];
  }

  hb_set_t *lookups = hb_set_create ();
  hb_ot_layout_collect_lookups (face, HB_OT_TAG_GSUB, NULL, NULL, removed ? kept : NULL, lookups);
  hb_ot_layout_lookups_substitute_closure (face, lookups, glyphs);
  hb_set_destroy (lookups);
  hb_set_destroy (seen);
  free (tags);
  free (kept);
}

static void plan_glyphs (hb_face_t *face, const char *spec, hb_set_t *gids, hb_set_t *out)
{
  hb_subset_input_t *input = hb_subset_input_create_or_fail ();
  hb_set_union (hb_subset_input_glyph_set (input), gids);
  hb_subset_input_set_flags (input, HB_SUBSET_FLAGS_RETAIN_GIDS);
  if (strcmp (spec, "default"))
    feature_filter (spec, hb_subset_input_set (input, HB_SUBSET_SETS_LAYOUT_FEATURE_TAG));
  hb_subset_plan_t *plan = hb_subset_plan_create_or_fail (face, input);
  if (plan)
  {
    hb_map_keys (hb_subset_plan_old_to_new_glyph_mapping (plan), out);
    hb_subset_plan_destroy (plan);
  }
  hb_subset_input_destroy (input);
}

int main (int argc, char **argv)
{
  if (argc != 2)
  {
    fprintf (stderr, "usage: hb-closure FONT < requests\n");
    return 2;
  }

  hb_blob_t *blob = hb_blob_create_from_file_or_fail (argv[1]);
  if (!blob)
  {
    fprintf (stderr, "cannot read %s\n", argv[1]);
    return 1;
  }

  hb_face_t *face = hb_face_create (blob, 0);
  hb_font_t *font = hb_font_create (face);
  static char line[1 << 20];
  while (fgets (line, sizeof line, stdin))
  {
    line[strcspn (line, "\r\n")] = 0;
    if (line[0] == 'N')
      printf ("%u", hb_face_get_glyph_count (face));
    else if (line[0] == 'M')
    {
      hb_buffer_t *buffer = hb_buffer_create ();
      hb_buffer_add_utf8 (buffer, line + 2, -1, 0, -1);
      unsigned len;
      hb_glyph_info_t *info = hb_buffer_get_glyph_infos (buffer, &len);
      int first = 1;
      for (unsigned i = 0; i < len; i++)
      {
        hb_codepoint_t g;
        if (hb_font_get_nominal_glyph (font, info[i].codepoint, &g))
        {
          printf (first ? "%u" : ",%u", g);
          first = 0;
        }
      }
      hb_buffer_destroy (buffer);
    }
    else if (line[0] == 'C')
    {
      char *spec = line + 2;
      char *gids_text = strchr (spec, ' ');
      if (gids_text)
        *gids_text++ = 0;
      else
        gids_text = spec + strlen (spec);
      hb_set_t *gids = hb_set_create ();
      parse_gids (gids_text, gids);
      hb_set_t *filter = hb_set_create ();
      feature_filter (spec, filter);
      hb_set_t *glyphs = hb_set_copy (gids);
      closure (face, filter, glyphs);
      print_set (glyphs);
      printf ("|");
      hb_set_t *planned = hb_set_create ();
      plan_glyphs (face, spec, gids, planned);
      print_set (planned);
      hb_set_destroy (planned);
      hb_set_destroy (glyphs);
      hb_set_destroy (filter);
      hb_set_destroy (gids);
    }

    printf ("\n");
    fflush (stdout);
  }

  hb_font_destroy (font);
  hb_face_destroy (face);
  hb_blob_destroy (blob);
  return 0;
}
