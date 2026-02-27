# INESSS Drug Evaluations Scraper

Custom connector that scrapes tabular data from the [INESSS medication evaluation page](https://www.inesss.qc.ca/thematiques/medicaments/medicaments-evaluation-aux-fins-dinscription.html).

## Overview

INESSS (Institut national d'excellence en santé et en services sociaux) publishes data about drugs under evaluation and already evaluated for the Quebec public drug formulary. This connector scrapes the five data tables available on their evaluation page and returns structured JSON.

## Operations

| Operation | Description | Data Size |
|-----------|-------------|-----------|
| **GetGeneriques** | Plan travail génériques — generic drugs under evaluation | ~30 rows |
| **GetInnovateurs** | Plan travail innovateurs — innovator drugs in continuous evaluation | ~140 rows |
| **GetAutresTravaux** | Autres travaux — miscellaneous evaluation work items | Small |
| **GetSollicitations** | Sollicitations — invitations to manufacturers for submissions | Small |
| **GetProduitsEvalues** | Produits évalués — products already evaluated (paginated, 30/page) | ~7000+ total |

## Output Format

All operations return JSON with this structure:

```json
{
  "tableName": "Plan travail génériques",
  "count": 31,
  "scrapedAt": "2026-02-26T12:00:00.0000000Z",
  "items": [
    {
      "nomCommercial": "AG-Perindopril/indapamide",
      "denominationCommune": "périndopril erbumine/indapamide",
      "nomFabricant": "Angita"
    }
  ]
}
```

### Produits évalués (paginated)

The `GetProduitsEvalues` operation includes pagination metadata:

```json
{
  "tableName": "Produits évalués",
  "page": 1,
  "totalResults": 7026,
  "resultsPerPage": 30,
  "count": 30,
  "scrapedAt": "...",
  "items": [
    {
      "date": "2026-02-18",
      "nomCommercialProjet": "Ilumya (psoriasis en plaques)",
      "nomCommercialProjetLink": "https://www.inesss.qc.ca/thematiques/...",
      "denominationCommuneSujet": "tildrakizumab",
      "denominationCommuneSujetLink": "https://www.inesss.qc.ca/thematiques/...",
      "fabricant": "Sun Pharma",
      "recommandationInesss": "Inscription - Sous conditions",
      "decisionMinistre": "À venir"
    }
  ]
}
```

To iterate all evaluated products in Power Automate, use a `Do until` loop incrementing the `page` parameter until `count` is 0 or `page * 30 >= totalResults`.

## Column Mapping

### GetGeneriques

| JSON Field | Source Column |
|-----------|---------------|
| `nomCommercial` | Nom commercial |
| `denominationCommune` | Dénomination commune |
| `nomFabricant` | Nom du fabricant |

### GetInnovateurs

| JSON Field | Source Column |
|-----------|---------------|
| `nomCommercial` | Nom commercial |
| `denominationCommune` | Dénomination commune |
| `nomFabricant` | Nom du fabricant |
| `indication` | Indication |
| `typeDemande` | Type de demande |
| `statutDemande` | **Statut de la demande |
| `dateLimiteCommentaires` | Date limite pour commentaires |

### GetAutresTravaux

| JSON Field | Source Column |
|-----------|---------------|
| `projet` | Projet |
| `sujet` | Sujet |
| `debutTravaux` | Début des travaux |
| `dateLimiteCommentaires` | Date limite pour commentaires |

### GetSollicitations

| JSON Field | Source Column |
|-----------|---------------|
| `nomCommercial` | Nom commercial |
| `denominationCommune` | Dénomination commune |
| `nomFabricant` | Nom du fabricant |
| `indication` | Indication |
| `typeDemande` | Type de demande |
| `statutDemande` | Statut de la demande |
| `dateSollicitation` | Date de sollicitation |

### GetProduitsEvalues

| JSON Field | Source Column |
|-----------|---------------|
| `date` | Date |
| `nomCommercialProjet` | Nom commercial / Projet (text) |
| `nomCommercialProjetLink` | Nom commercial / Projet (link URL) |
| `denominationCommuneSujet` | Dénomination commune / Sujet (text) |
| `denominationCommuneSujetLink` | Dénomination commune / Sujet (link URL) |
| `fabricant` | Fabricant |
| `recommandationInesss` | Recommandation de l'INESSS |
| `decisionMinistre` | Décision du Ministre |

## Technical Notes

- **No authentication required** — the INESSS page is publicly accessible
- **HTML scraping via regex** — uses `data-mobile` attributes on `<td>` elements for reliable extraction
- **Tabs 1–4** are fully loaded in the initial page HTML (single HTTP request)
- **Tab 5 (Produits évalués)** uses server-side Solr pagination (`?tx_solr[page]=N`)
- **Full page size** is ~630 KB; string processing completes well within the 2-minute timeout
- **Links** in Produits évalués cells are converted to absolute URLs (`https://www.inesss.qc.ca/...`)
- Column name matching handles variations (extra `**` prefixes, irregular whitespace) via normalization

## Deployment

Follow the standard deployment process:

1. Copy the connector files to Power Automate portal
2. No authentication configuration needed (set to "No authentication")
3. The `host` in the swagger must be `www.inesss.qc.ca` — actual URLs are constructed in the script
4. All five `scriptOperations` must be listed in `apiProperties.json`

## Status

Ready
