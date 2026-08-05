import { useEffect, useState } from "react";
import { ApiError, apiGet } from "./client";

export interface Warehouse {
  id: string;
  name: string;
  code: string;
}

export interface Branch {
  id: string;
  name: string;
  warehouses: Warehouse[];
}

export interface Company {
  id: string;
  name: string;
  branches: Branch[];
}

interface OrganizationResponse {
  companies: Company[];
}

/** The tenant's own company/branch/warehouse structure, for populating pickers. */
export function useOrganization() {
  const [companies, setCompanies] = useState<Company[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiGet<OrganizationResponse>("/api/v1/organization")
      .then((response) => setCompanies(response.companies))
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load organization."));
  }, []);

  return { companies, error };
}
